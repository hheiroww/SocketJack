using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net.Database {
    /// <summary>Managed MySQL classic-protocol connector. TLS is mandatory. Supports text queries and multiple results.</summary>
    public sealed class MySqlDataClient : ISqlConnector {
        public string Server { get; set; } = "localhost";
        public int Port { get; set; } = 3306;
        public string Database { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public bool TrustServerCertificate { get; set; }
        public int TimeoutSeconds { get; set; } = 30;
        public SqlDialect Dialect => SqlDialect.MySql;
        private System.Net.Sockets.TcpClient client;
        private SslStream stream;
        private readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
        private const uint Capabilities=1|4|8|512|2048|8192|32768|65536|131072|524288;
        public async Task OpenAsync(CancellationToken cancellationToken=default) {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await OpenCore(cancellationToken).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
        private async Task OpenCore(CancellationToken cancellationToken) {
            if(stream!=null) return;
            client=new System.Net.Sockets.TcpClient { ReceiveTimeout=checked(TimeoutSeconds*1000), SendTimeout=checked(TimeoutSeconds*1000), NoDelay=true };
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
                timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
                using(timeout.Token.Register(()=>client?.Dispose())) {
                    try {
                        await client.ConnectAsync(Server,Port).ConfigureAwait(false);
                        var network=client.GetStream(); byte sequence=0;
                        var greeting=MySqlProtocolServer.ReadPacket(network,ref sequence);
                        byte[] scramble; string plugin; uint available;
                        using(var input=new MemoryStream(greeting)) using(var reader=new BinaryReader(input)) {
                            if(reader.ReadByte()!=10) throw new InvalidDataException("MySQL protocol 10 required.");
                            MySqlProtocolServer.ReadCString(reader); reader.ReadUInt32(); var first=reader.ReadBytes(8); reader.ReadByte(); available=reader.ReadUInt16();
                            reader.ReadByte(); reader.ReadUInt16(); available|=(uint)reader.ReadUInt16()<<16; int authLength=reader.ReadByte(); reader.ReadBytes(10);
                            var tail=reader.ReadBytes(Math.Max(13,authLength-8)); scramble=first.Concat(tail.Take(12)).ToArray();
                            plugin=input.Position<input.Length?MySqlProtocolServer.ReadCString(reader):"mysql_native_password";
                        }
                        if((available&2048)==0) throw new AuthenticationException("MySQL server does not offer TLS.");
                        uint flags=Capabilities&available;
                        byte[] prefix;
                        using(var buffer=new MemoryStream()) using(var writer=new BinaryWriter(buffer)) { writer.Write(flags); writer.Write((uint)16777214); writer.Write((byte)45); writer.Write(new byte[23]); prefix=buffer.ToArray(); }
                        MySqlProtocolServer.WritePacket(network,prefix,ref sequence);
                        stream=new SslStream(network,false,(_,__,___,errors)=>TrustServerCertificate||errors==SslPolicyErrors.None);
                        await stream.AuthenticateAsClientAsync(Server,null,SslProtocols.Tls12,false).ConfigureAwait(false);
                        using(var buffer=new MemoryStream()) using(var writer=new BinaryWriter(buffer)) {
                            writer.Write(prefix); MySqlProtocolServer.CString(writer,Username); var auth=Scramble(plugin,scramble,Password); writer.Write((byte)auth.Length); writer.Write(auth);
                            if((flags&8)!=0) MySqlProtocolServer.CString(writer,Database); if((flags&524288)!=0) MySqlProtocolServer.CString(writer,plugin);
                            MySqlProtocolServer.WritePacket(stream,buffer.ToArray(),ref sequence);
                        }
                        while(true) {
                            var packet=MySqlProtocolServer.ReadPacket(stream,ref sequence); CheckError(packet);
                            if(packet[0]==0) break;
                            if(packet[0]==254) {
                                using(var buffer=new MemoryStream(packet,1,packet.Length-1)) using(var reader=new BinaryReader(buffer)) { plugin=MySqlProtocolServer.ReadCString(reader); scramble=reader.ReadBytes((int)(buffer.Length-buffer.Position)); if(scramble.Length>20) scramble=scramble.Take(20).ToArray(); }
                                MySqlProtocolServer.WritePacket(stream,Scramble(plugin,scramble,Password),ref sequence);
                            } else if(packet.Length==2 && packet[0]==1 && packet[1]==4 && plugin=="caching_sha2_password") {
                                var secret=Encoding.UTF8.GetBytes(Password+"\0"); try { MySqlProtocolServer.WritePacket(stream,secret,ref sequence); } finally { Array.Clear(secret,0,secret.Length); }
                            } else if(packet.Length==2 && packet[0]==1 && packet[1]==3) { }
                            else throw new AuthenticationException("Unsupported MySQL authentication exchange.");
                        }
                    } catch { Close(); throw; }
                }
            }
        }
        private static byte[] Scramble(string plugin,byte[] seed,string password) {
            if(password.Length==0) return Array.Empty<byte>();
            byte[] first,second,mask;
            if(plugin=="caching_sha2_password") { using(var hash=SHA256.Create()) { first=hash.ComputeHash(Encoding.UTF8.GetBytes(password)); second=hash.ComputeHash(first); mask=hash.ComputeHash(second.Concat(seed).ToArray()); } }
            else if(plugin=="mysql_native_password") { using(var hash=SHA1.Create()) { first=hash.ComputeHash(Encoding.UTF8.GetBytes(password)); second=hash.ComputeHash(first); mask=hash.ComputeHash(seed.Concat(second).ToArray()); } }
            else throw new AuthenticationException("Unsupported authentication plugin: "+plugin);
            for(int i=0;i<first.Length;i++) first[i]^=mask[i]; return first;
        }
        public async Task<IReadOnlyList<QueryResult>> ExecuteAsync(SqlCommand command,CancellationToken cancellationToken=default) {
            if(command==null) throw new ArgumentNullException(nameof(command));
            if(command.Parameters.Count>0) throw new SqlExecutionException("Binary prepared parameters are not implemented by MySqlDataClient; use an AdoSqlConnector with a MySQL provider for parameterized commands.","0A000");
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try {
                await OpenCore(cancellationToken).ConfigureAwait(false);
                using(cancellationToken.Register(Close)) {
                    byte sequence=0;
                    MySqlProtocolServer.WritePacket(stream,new byte[]{3}.Concat(Encoding.UTF8.GetBytes(command.Text)).ToArray(),ref sequence);
                    var results=new List<QueryResult>(); ushort status;
                    do {
                        var packet=MySqlProtocolServer.ReadPacket(stream,ref sequence); CheckError(packet);
                        var result=new QueryResult { Handled=true }; results.Add(result);
                        if(packet[0]==0) { using(var data=new MemoryStream(packet)) using(var reader=new BinaryReader(data)) { reader.ReadByte(); result.RowsAffected=(long)Length(reader); Length(reader); status=reader.ReadUInt16(); } continue; }
                        int count; using(var data=new MemoryStream(packet)) using(var reader=new BinaryReader(data)) count=checked((int)Length(reader));
                        result.HasResultSet=true; var codes=new List<byte>();
                        for(int i=0;i<count;i++) {
                            packet=MySqlProtocolServer.ReadPacket(stream,ref sequence); CheckError(packet);
                            using(var data=new MemoryStream(packet)) using(var reader=new BinaryReader(data)) {
                                Text(reader); Text(reader); Text(reader); Text(reader); result.Columns.Add(Text(reader)); Text(reader); reader.ReadByte(); reader.ReadUInt16(); reader.ReadUInt32(); byte code=reader.ReadByte(); codes.Add(code);
                                result.DataTypes.Add(code==3?typeof(int):code==8?typeof(long):code==246?typeof(decimal):code==5||code==4?typeof(double):typeof(string));
                            }
                        }
                        packet=MySqlProtocolServer.ReadPacket(stream,ref sequence); if(packet[0]!=254) throw new InvalidDataException("Expected MySQL metadata EOF.");
                        while(true) {
                            packet=MySqlProtocolServer.ReadPacket(stream,ref sequence); CheckError(packet);
                            if(packet[0]==254 && packet.Length<9) { status=BitConverter.ToUInt16(packet,3); break; }
                            using(var data=new MemoryStream(packet)) using(var reader=new BinaryReader(data)) {
                                var row=new object[count]; for(int i=0;i<count;i++) { string text=Text(reader); row[i]=text==null?null:result.DataTypes[i]==typeof(string)?text:Convert.ChangeType(text,result.DataTypes[i],CultureInfo.InvariantCulture); } result.Rows.Add(row);
                            }
                        }
                        result.RowsAffected=result.Rows.Count;
                    } while((status&8)!=0);
                    return results;
                }
            } catch(SqlExecutionException) { throw; } catch { Close(); throw; }
            finally { gate.Release(); }
        }
        private static ulong Length(BinaryReader reader) { byte first=reader.ReadByte(); if(first<251) return first; if(first==252) return reader.ReadUInt16(); if(first==253) return (ulong)(reader.ReadByte()|reader.ReadByte()<<8|reader.ReadByte()<<16); if(first==254) return reader.ReadUInt64(); throw new InvalidDataException("Invalid MySQL length."); }
        private static string Text(BinaryReader reader) { if(reader.PeekChar()==251) { reader.ReadByte(); return null; } ulong length=Length(reader); if(length>16777214) throw new InvalidDataException("MySQL text too large."); var bytes=reader.ReadBytes((int)length); if(bytes.Length!=(int)length) throw new EndOfStreamException(); return Encoding.UTF8.GetString(bytes); }
        private static void CheckError(byte[] packet) { if(packet.Length==0) throw new InvalidDataException("Empty MySQL response."); if(packet[0]!=255) return; int code=BitConverter.ToUInt16(packet,1); string state=packet.Length>=9&&packet[3]=='#'?Encoding.ASCII.GetString(packet,4,5):"HY000"; int offset=state=="HY000"?3:9; throw new SqlExecutionException(Encoding.UTF8.GetString(packet,offset,packet.Length-offset),state,code); }
        private void Close() { stream?.Dispose(); client?.Dispose(); stream=null; client=null; }
        public void Dispose() { Close(); gate.Dispose(); }
    }
}
