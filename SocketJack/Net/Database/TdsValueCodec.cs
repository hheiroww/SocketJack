using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace SocketJack.Net.Database {
    internal static class TdsValueCodec {
        internal sealed class ValueType {
            internal byte Token, Size, Precision, Scale;
            internal ushort MaxLength;
        }
        internal static ValueType ReadType(BinaryReader reader) {
            var type = new ValueType { Token = reader.ReadByte() };
            switch (type.Token) {
                case 0x26: case 0x68: case 0x6D: case 0x24: type.Size = reader.ReadByte(); break;
                case 0x6A: case 0x6C: type.Size = reader.ReadByte(); type.Precision = reader.ReadByte(); type.Scale = reader.ReadByte(); break;
                case 0xE7: case 0xEF: case 0xA7: case 0xAF: type.MaxLength = reader.ReadUInt16(); Bytes(reader,5); break;
                case 0xA5: case 0xAD: type.MaxLength = reader.ReadUInt16(); break;
                case 0x38: type.Size=4; break; case 0x7F: type.Size=8; break; case 0x34: type.Size=2; break; case 0x30: case 0x32: type.Size=1; break;
                default: throw new SqlExecutionException("Unsupported TDS parameter type: " + type.Token.ToString("X2"), "0A000");
            }
            return type;
        }
        internal static object ReadValue(BinaryReader reader, ValueType type) {
            if (type.Token == 0xE7 || type.Token == 0xEF || type.Token == 0xA7 || type.Token == 0xAF || type.Token == 0xA5 || type.Token == 0xAD) {
                byte[] bytes;
                if (type.MaxLength == ushort.MaxValue) {
                    ulong total = reader.ReadUInt64(); if (total == ulong.MaxValue) return null;
                    using(var stream = new MemoryStream()) {
                        while(true) { uint length = reader.ReadUInt32(); if (length == 0) break; if (length > 16*1024*1024 || stream.Length+length > 16*1024*1024) throw new InvalidDataException("TDS value exceeds limit."); var chunk=Bytes(reader,(int)length); stream.Write(chunk,0,chunk.Length); }
                        bytes = stream.ToArray();
                    }
                } else { ushort length=reader.ReadUInt16(); if(length==ushort.MaxValue) return null; bytes=Bytes(reader,length); }
                if(type.Token==0xA5 || type.Token==0xAD) return bytes;
                return (type.Token==0xE7 || type.Token==0xEF ? Encoding.Unicode : Encoding.GetEncoding(1252)).GetString(bytes);
            }
            bool fixedType = type.Token==0x38 || type.Token==0x7F || type.Token==0x34 || type.Token==0x30 || type.Token==0x32;
            int size = fixedType ? type.Size : reader.ReadByte(); if(size==0) return null;
            byte[] data=Bytes(reader,size);
            switch(type.Token) {
                case 0x26: case 0x38: case 0x7F: case 0x34: case 0x30:
                    return size==1 ? (object)data[0] : size==2 ? (object)BitConverter.ToInt16(data,0) : size==4 ? (object)BitConverter.ToInt32(data,0) : size==8 ? (object)BitConverter.ToInt64(data,0) : throw new InvalidDataException("Invalid TDS integer size.");
                case 0x68: case 0x32: return data[0]!=0;
                case 0x6D: return size==4 ? (object)BitConverter.ToSingle(data,0) : BitConverter.ToDouble(data,0);
                case 0x24: return new Guid(data);
                case 0x6A: case 0x6C:
                    var unsigned = new byte[data.Length]; Array.Copy(data,1,unsigned,0,data.Length-1);
                    decimal number=(decimal)new BigInteger(unsigned); for(int i=0;i<type.Scale;i++) number/=10; return data[0]==0 ? -number : number;
                default: throw new InvalidDataException("Unsupported TDS value.");
            }
        }
        private static byte[] Bytes(BinaryReader reader,int count) { var bytes=reader.ReadBytes(count); if(bytes.Length!=count) throw new EndOfStreamException(); return bytes; }
        internal static void WriteResult(BinaryWriter writer, QueryResult result) {
            var types = new List<(Type type, byte scale)>();
            for(int i=0;i<result.Columns.Count;i++) {
                Type type = result.DataTypes.Count>i ? result.DataTypes[i] : result.ColumnTypes?.Count>i && result.ColumnTypes[i]==0x38 ? typeof(int) : typeof(string);
                byte scale=0;
                if(type==typeof(decimal)) foreach(var row in result.Rows) if(row[i]!=null) scale=Math.Max(scale,(byte)((decimal.GetBits(Convert.ToDecimal(row[i],CultureInfo.InvariantCulture))[3]>>16)&0x7f));
                types.Add((type,scale));
            }
            writer.Write((byte)0x81); writer.Write((ushort)result.Columns.Count);
            for(int i=0;i<types.Count;i++) {
                var t=types[i]; writer.Write((uint)0); writer.Write((ushort)1);
                if(t.type==typeof(int)||t.type==typeof(short)||t.type==typeof(long)||t.type==typeof(byte)) { writer.Write((byte)0x26); writer.Write((byte)(t.type==typeof(long)?8:t.type==typeof(short)?2:t.type==typeof(byte)?1:4)); }
                else if(t.type==typeof(bool)) { writer.Write((byte)0x68); writer.Write((byte)1); }
                else if(t.type==typeof(float)||t.type==typeof(double)) { writer.Write((byte)0x6D); writer.Write((byte)8); }
                else if(t.type==typeof(decimal)) { writer.Write((byte)0x6A); writer.Write((byte)17); writer.Write((byte)38); writer.Write(t.scale); }
                else if(t.type==typeof(Guid)) { writer.Write((byte)0x24); writer.Write((byte)16); }
                else if(t.type==typeof(byte[])) { writer.Write((byte)0xA5); writer.Write(ushort.MaxValue); }
                else { writer.Write((byte)0xE7); writer.Write(ushort.MaxValue); writer.Write(new byte[]{9,4,0,0,0}); }
                string name=result.Columns[i]??""; if(name.Length>255) throw new InvalidDataException("Column name exceeds TDS limit."); writer.Write((byte)name.Length); writer.Write(Encoding.Unicode.GetBytes(name));
            }
            foreach(var row in result.Rows) {
                if(row.Length!=types.Count) throw new InvalidDataException("Result column count mismatch."); writer.Write((byte)0xD1);
                for(int i=0;i<types.Count;i++) {
                    var t=types[i]; object value=ManagedSqlEngine.Unwrap(row[i]);
                    bool variable=!(t.type==typeof(int)||t.type==typeof(short)||t.type==typeof(long)||t.type==typeof(byte)||t.type==typeof(bool)||t.type==typeof(float)||t.type==typeof(double)||t.type==typeof(decimal)||t.type==typeof(Guid));
                    if(value==null) { if(variable) writer.Write(ulong.MaxValue); else writer.Write((byte)0); continue; }
                    if(t.type==typeof(int)) { writer.Write((byte)4); writer.Write(Convert.ToInt32(value)); }
                    else if(t.type==typeof(long)) { writer.Write((byte)8); writer.Write(Convert.ToInt64(value)); }
                    else if(t.type==typeof(short)) { writer.Write((byte)2); writer.Write(Convert.ToInt16(value)); }
                    else if(t.type==typeof(byte)) { writer.Write((byte)1); writer.Write(Convert.ToByte(value)); }
                    else if(t.type==typeof(bool)) { writer.Write((byte)1); writer.Write(Convert.ToBoolean(value)); }
                    else if(t.type==typeof(float)||t.type==typeof(double)) { writer.Write((byte)8); writer.Write(Convert.ToDouble(value)); }
                    else if(t.type==typeof(Guid)) { writer.Write((byte)16); writer.Write(((Guid)value).ToByteArray()); }
                    else if(t.type==typeof(decimal)) {
                        decimal number=Convert.ToDecimal(value,CultureInfo.InvariantCulture); var bits=decimal.GetBits(number);
                        BigInteger magnitude=(uint)bits[0]+((BigInteger)(uint)bits[1]<<32)+((BigInteger)(uint)bits[2]<<64);
                        int scale=(bits[3]>>16)&0x7f; magnitude*=BigInteger.Pow(10,t.scale-scale);
                        if(magnitude>=BigInteger.Pow(10,38)) throw new OverflowException("Decimal precision exceeds TDS decimal(38,scale).");
                        byte[] bytes=magnitude.ToByteArray(); writer.Write((byte)17); writer.Write((byte)(number<0?0:1)); for(int n=0;n<16;n++) writer.Write(n<bytes.Length?bytes[n]:(byte)0);
                    } else {
                        byte[] bytes=value is byte[] binary ? binary : Encoding.Unicode.GetBytes(Convert.ToString(value,CultureInfo.InvariantCulture));
                        writer.Write((ulong)bytes.Length); if(bytes.Length>0) { writer.Write((uint)bytes.Length); writer.Write(bytes); } writer.Write((uint)0);
                    }
                }
            }
        }
    }
}
