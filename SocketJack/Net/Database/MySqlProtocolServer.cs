using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net.Database {
    /// <summary>Opt-in MySQL classic-protocol listener over the managed DataServer.
    /// Uses caching_sha2_password full authentication over TLS; no plaintext password exchange is allowed.</summary>
    public sealed class MySqlProtocolServer : IDisposable {
        private readonly DataServer server;
        private readonly TcpListener listener;
        private readonly ConcurrentDictionary<System.Net.Sockets.TcpClient,byte> clients=new ConcurrentDictionary<System.Net.Sockets.TcpClient,byte>();
        private bool stopped;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public event Action<Exception> ConnectionError;
        private const uint Capabilities = 1|4|8|512|2048|8192|32768|131072|524288;
        public MySqlProtocolServer(DataServer server, int port=3306, IPAddress address=null) {
            this.server=server ?? throw new ArgumentNullException(nameof(server));
            listener=new TcpListener(address??IPAddress.Loopback,port);
        }
        public void Start() { if(stopped) throw new ObjectDisposedException(nameof(MySqlProtocolServer)); listener.Start(); _=Accept(); }
        private async Task Accept() {
            while(!stopped) {
                System.Net.Sockets.TcpClient client;
                try { client=await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch(ObjectDisposedException) { break; } catch(SocketException) when(stopped) { break; }
                clients[client]=0; _=Task.Run(()=>Serve(client));
            }
        }
        private void Serve(System.Net.Sockets.TcpClient client) {
            var session=new SqlSession { ConnectionId=Guid.NewGuid(), CurrentDatabase=server.DefaultDatabase };
            try {
                client.ReceiveTimeout=30000; client.SendTimeout=30000; client.NoDelay=true;
                using(var network=client.GetStream()) {
                    Stream stream=network; byte sequence=0;
                    byte[] scramble=new byte[20]; using(var rng=RandomNumberGenerator.Create()) rng.GetBytes(scramble);
                    using(var buffer=new MemoryStream()) using(var writer=new BinaryWriter(buffer)) {
                        writer.Write((byte)10); CString(writer,"8.4.0-SocketJack"); writer.Write((uint)session.ConnectionId.GetHashCode()); writer.Write(scramble,0,8); writer.Write((byte)0);
                        writer.Write((ushort)(Capabilities & 65535)); writer.Write((byte)45); writer.Write((ushort)2); writer.Write((ushort)(Capabilities>>16)); writer.Write((byte)21); writer.Write(new byte[10]); writer.Write(scramble,8,12); writer.Write((byte)0); CString(writer,"caching_sha2_password");
                        WritePacket(stream,buffer.ToArray(),ref sequence);
                    }
                    var request=ReadPacket(stream,ref sequence);
                    if(request.Length!=32 || (BitConverter.ToUInt32(request,0)&2048)==0) { Error(stream,ref sequence,"TLS is required.","08004",1045); return; }
                    using(var tls=new SslStream(network,leaveInnerStreamOpen:true)) {
                        tls.AuthenticateAsServer(server.Certificate,false,SslProtocols.Tls12,false); stream=tls;
                        request=ReadPacket(stream,ref sequence);
                        string username,database=null;
                        using(var input=new MemoryStream(request)) using(var reader=new BinaryReader(input)) {
                            uint flags=reader.ReadUInt32(); if((flags&512)==0) throw new InvalidDataException("Protocol 4.1 required."); input.Position=32;
                            username=ReadCString(reader); int authLength=reader.ReadByte(); if(reader.ReadBytes(authLength).Length!=authLength) throw new EndOfStreamException();
                            if((flags&8)!=0) database=ReadCString(reader);
                        }
                        using(var challenge=new MemoryStream()) using(var writer=new BinaryWriter(challenge)) {
                            writer.Write((byte)254); CString(writer,"caching_sha2_password"); writer.Write(scramble); writer.Write((byte)0); WritePacket(stream,challenge.ToArray(),ref sequence);
                        }
                        ReadPacket(stream,ref sequence); // client scramble response; full auth is required
                        WritePacket(stream,new byte[]{1,4},ref sequence); // request full auth inside TLS
                        var password=ReadPacket(stream,ref sequence);
                        string secret=Encoding.UTF8.GetString(password).TrimEnd('\0');
                        string address=((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                        bool authenticated=server.IsSqlLoginIpAllowed(address)&&server.Authenticate(username,secret);
                        Array.Clear(password,0,password.Length); secret=null;
                        if(!authenticated) { Error(stream,ref sequence,"Access denied.","28000",1045); return; }
                        session.Username=username; session.IsAuthenticated=true;
                        if(!string.IsNullOrEmpty(database)) server.Execute(session,new SqlCommand("USE `"+database.Replace("`","``")+"`") { Dialect=SqlDialect.MySql });
                        Ok(stream,ref sequence,0,2);
                        while(!stopped) {
                            sequence=0; request=ReadPacket(stream,ref sequence); if(request.Length==0) throw new InvalidDataException("Empty command.");
                            try {
                                switch(request[0]) {
                                    case 1: return;
                                    case 14: Ok(stream,ref sequence,0,Status(session)); break;
                                    case 31: session.Transaction=null; Ok(stream,ref sequence,0,2); break;
                                    case 2:
                                        string db=Encoding.UTF8.GetString(request,1,request.Length-1);
                                        server.Execute(session,new SqlCommand("USE `"+db.Replace("`","``")+"`") { Dialect=SqlDialect.MySql }); Ok(stream,ref sequence,0,Status(session)); break;
                                    case 3:
                                        string sql=Encoding.UTF8.GetString(request,1,request.Length-1);
                                        var results=server.Execute(session,new SqlCommand(sql) { Dialect=SqlDialect.MySql });
                                        for(int i=0;i<results.Count;i++) WriteResult(stream,ref sequence,results[i],(ushort)(Status(session)|(i+1<results.Count?8:0)),session.CurrentDatabase);
                                        break;
                                    default: Error(stream,ref sequence,"MySQL command is not implemented.","0A000",1235); break;
                                }
                            } catch(SqlExecutionException ex) { Error(stream,ref sequence,ex.Message,ex.SqlState,1064); }
                            catch(Exception ex) when(ex is FormatException || ex is OverflowException || ex is DivideByZeroException) { Error(stream,ref sequence,ex.Message,"22000",1366); }
                        }
                    }
                }
            } catch(Exception ex) { if(!stopped) ConnectionError?.Invoke(ex); }
            finally { session.Transaction=null; clients.TryRemove(client,out _); client.Dispose(); }
        }
        private static ushort Status(SqlSession session) => (ushort)(session.Transaction==null?2:3);
        private static void Ok(Stream stream,ref byte sequence,long affected,ushort status) {
            using(var buffer=new MemoryStream()) using(var writer=new BinaryWriter(buffer)) { writer.Write((byte)0); Len(writer,(ulong)Math.Max(0,affected)); writer.Write((byte)0); writer.Write(status); writer.Write((ushort)0); WritePacket(stream,buffer.ToArray(),ref sequence); }
        }
        private static void Error(Stream stream,ref byte sequence,string message,string state,int code) {
            using(var buffer=new MemoryStream()) using(var writer=new BinaryWriter(buffer)) { writer.Write((byte)255); writer.Write((ushort)code); writer.Write((byte)'#'); writer.Write(Encoding.ASCII.GetBytes(state)); writer.Write(Encoding.UTF8.GetBytes(message)); WritePacket(stream,buffer.ToArray(),ref sequence); }
        }
        private static void Eof(Stream stream,ref byte sequence,ushort status) => WritePacket(stream,new byte[]{254,0,0,(byte)status,(byte)(status>>8)},ref sequence);
        private static void WriteResult(Stream stream,ref byte sequence,QueryResult result,ushort status,string database) {
            if(!result.HasResultSet) { Ok(stream,ref sequence,result.RowsAffected,status); return; }
            using(var buffer=new MemoryStream()) using(var writer=new BinaryWriter(buffer)) { Len(writer,(ulong)result.Columns.Count); WritePacket(stream,buffer.ToArray(),ref sequence); }
            for(int i=0;i<result.Columns.Count;i++) {
                Type type=result.DataTypes.Count>i?result.DataTypes[i]:typeof(string);
                byte code=type==typeof(int)?(byte)3:type==typeof(long)?(byte)8:type==typeof(decimal)?(byte)246:type==typeof(double)||type==typeof(float)?(byte)5:type==typeof(bool)?(byte)1:(byte)253;
                using(var buffer=new MemoryStream()) using(var writer=new BinaryWriter(buffer)) {
                    Text(writer,"def"); Text(writer,database??""); Text(writer,""); Text(writer,""); Text(writer,result.Columns[i]); Text(writer,result.Columns[i]); writer.Write((byte)12); writer.Write((ushort)45); writer.Write((uint)16777215); writer.Write(code); writer.Write((ushort)0); writer.Write((byte)(type==typeof(decimal)?28:0)); writer.Write((ushort)0); WritePacket(stream,buffer.ToArray(),ref sequence);
                }
            }
            Eof(stream,ref sequence,status);
            foreach(var row in result.Rows) using(var buffer=new MemoryStream()) using(var writer=new BinaryWriter(buffer)) {
                foreach(var raw in row) { var value=ManagedSqlEngine.Unwrap(raw); if(value==null) writer.Write((byte)251); else Text(writer,value is bool boolean ? boolean?"1":"0":Convert.ToString(value,CultureInfo.InvariantCulture)); }
                WritePacket(stream,buffer.ToArray(),ref sequence);
            }
            Eof(stream,ref sequence,status);
        }
        internal static void CString(BinaryWriter writer,string value) { writer.Write(Encoding.UTF8.GetBytes(value)); writer.Write((byte)0); }
        internal static string ReadCString(BinaryReader reader) { using(var buffer=new MemoryStream()) { byte value; while((value=reader.ReadByte())!=0) { if(buffer.Length>1024*1024) throw new InvalidDataException("String too long."); buffer.WriteByte(value); } return Encoding.UTF8.GetString(buffer.ToArray()); } }
        internal static void Text(BinaryWriter writer,string value) { var bytes=Encoding.UTF8.GetBytes(value??""); Len(writer,(ulong)bytes.Length); writer.Write(bytes); }
        internal static void Len(BinaryWriter writer,ulong value) { if(value<251) writer.Write((byte)value); else if(value<=65535) { writer.Write((byte)252); writer.Write((ushort)value); } else if(value<=16777215) { writer.Write((byte)253); writer.Write((byte)value); writer.Write((byte)(value>>8)); writer.Write((byte)(value>>16)); } else { writer.Write((byte)254); writer.Write(value); } }
        internal static byte[] ReadPacket(Stream stream,ref byte sequence) {
            byte[] header=ReadExact(stream,4); int length=header[0]|header[1]<<8|header[2]<<16;
            if(header[3]!=sequence++) throw new InvalidDataException("MySQL sequence mismatch.");
            if(length>=16777215) throw new InvalidDataException("MySQL multi-packet messages are not implemented.");
            return ReadExact(stream,length);
        }
        internal static byte[] ReadExact(Stream stream,int length) { var data=new byte[length]; for(int offset=0;offset<length;) { int count=stream.Read(data,offset,length-offset); if(count==0) throw new EndOfStreamException(); offset+=count; } return data; }
        internal static void WritePacket(Stream stream,byte[] data,ref byte sequence) { if(data.Length>=16777215) throw new InvalidDataException("MySQL packet too large."); byte[] header={(byte)data.Length,(byte)(data.Length>>8),(byte)(data.Length>>16),sequence++}; stream.Write(header,0,4); stream.Write(data,0,data.Length); stream.Flush(); }
        public void Dispose() { stopped=true; listener.Stop(); foreach(var client in clients.Keys) client.Dispose(); }
    }
}
