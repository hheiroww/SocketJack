using SocketJack.Net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SocketJack.Extensions {
    public static class ByteExtensions {

        private static readonly long firstSegmentId = CreateSegmentSeed();
        private static long nextSegmentId = firstSegmentId;
        private static long CreateSegmentSeed() {
            byte[] seed = new byte[8];
            using (var random = System.Security.Cryptography.RandomNumberGenerator.Create()) random.GetBytes(seed);
            return BitConverter.ToInt64(seed, 0);
        }
        private static string NextSegmentId() {
            long next = System.Threading.Interlocked.Increment(ref nextSegmentId);
            if (next == firstSegmentId) throw new InvalidOperationException("Segment object identifiers exhausted.");
            return unchecked((ulong)next).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }
        public static Segment[] GetSegments(this byte[] Bytes) => BuildSegments(Bytes, 4000, false);
        /// <summary>Split an object into bounded chunks. Segment indexes restart at one for each legacy object envelope.</summary>
        public static Segment[] GetSegments(this byte[] Bytes, int segmentSize) => BuildSegments(Bytes, segmentSize, false);
        public static Segment[] GetTerminatedSegments(this byte[] Bytes) => BuildSegments(Bytes, 4000, true);
        private static Segment[] BuildSegments(byte[] bytes, int size, bool terminated) {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (size < 1 || size > 32768) throw new ArgumentOutOfRangeException(nameof(size));
            int count = bytes.Length == 0 ? 0 : 1 + (bytes.Length - 1) / size;
            var segments = new Segment[count];
            if (count == 0) return segments;
            string id = NextSegmentId();
            for (int i = 0; i < count; i++) {
                int offset = i * size, length = Math.Min(size, bytes.Length - offset);
                string data;
                if (terminated) {
                    byte[] framed = new byte[length + 15]; WriteLength(framed, length);
                    Buffer.BlockCopy(bytes, offset, framed, 15, length);
                    data = Convert.ToBase64String(framed);
                } else data = Convert.ToBase64String(bytes, offset, length);
                // Legacy envelopes are one-based; reliable UDP fragments are zero-based.
                // Both counters restart for every object and never count across objects.
                segments[i] = new Segment { SID = id, Data = data, Index = i + 1, Count = count };
            }
            return segments;
        }
        private static void WriteLength(byte[] target, int length) {
            int position = 14;
            do { target[position--] = (byte)('0' + length % 10); length /= 10; } while (length != 0);
        }

        public static Segment[] GetSegments<T>(this byte[] SerializedBytes) {
            return SerializedBytes.GetSegments();
        }

        public static Segment[] GetTerminatedSegments<T>(this byte[] SerializedBytes) {
            return SerializedBytes.GetTerminatedSegments();
        }

        public static byte[] Terminate(this byte[] Data) {
            if (Data == null) throw new ArgumentNullException(nameof(Data));
            var framed = new byte[checked(Data.Length + 15)];
            WriteLength(framed, Data.Length); Buffer.BlockCopy(Data, 0, framed, 15, Data.Length);
            return framed;
        }

        /// <summary>
        /// Remove bytes from source Array.
        /// </summary>
        /// <param name="byteArray"></param>
        /// <param name="startIndex"></param>
        /// <param name="length"></param>
        /// <returns>New byte array with removed bytes between startIndex and length.</returns>
        public static byte[] Remove(this byte[] byteArray, int startIndex, int length) {
            if (startIndex < 0 || length < 0) {
                throw new ArgumentOutOfRangeException("Invalid start index or length.");
            } else if(startIndex + length >= byteArray.Length) {
                return null;
            }
            byte[] newArray = new byte[(byteArray.Length - length)];
            Array.Copy(byteArray, 0, newArray, 0, startIndex);
            Array.Copy(byteArray, startIndex + length, newArray, startIndex, byteArray.Length - (startIndex + length));
            return newArray;
        }

        /// <summary>
        /// Remove bytes from source Array.
        /// </summary>
        /// <param name="byteArray"></param>
        /// <param name="startIndex"></param>
        /// <param name="length"></param>
        /// <returns>New byte array with removed bytes from startIndex to end.</returns>
        public static byte[] Remove(this byte[] byteArray, int startIndex) {
            int length = byteArray.Length - startIndex;
            if (startIndex < 0 || length < 0 || startIndex + length > byteArray.Length) {
                throw new ArgumentOutOfRangeException("Invalid start index or length.");
            }
            byte[] newArray = new byte[(byteArray.Length - length)];
            Array.Copy(byteArray, 0, newArray, 0, startIndex);
            Array.Copy(byteArray, startIndex + length, newArray, startIndex, byteArray.Length - (startIndex + length));
            return newArray;
        }

        /// <summary>
        /// Byte Array equivalent of Substring.
        /// </summary>
        /// <param name="SourceArray"></param>
        /// <param name="startIndex"></param>
        /// <returns>Byte array between startIndex to the end of the array.</returns>
        public static byte[] Part(this byte[] sourceArray, int startIndex) {
            return sourceArray.Part(startIndex, sourceArray.Length);
        }

        /// <summary>
        /// Byte Array equivalent of Substring.
        /// </summary>
        /// <param name="SourceArray"></param>
        /// <param name="startIndex"></param>
        /// <returns>Byte array between startIndex to the end of the array.</returns>
        public static byte[] Part(this List<byte> sourceArray, int startIndex) {
            return sourceArray.Part(startIndex, sourceArray.Count);
        }

        /// <summary>
        /// Byte Array equivalent of Substring.
        /// </summary>
        /// <param name="SourceArray"></param>
        /// <param name="startIndex"></param>
        /// <param name="Length"></param>
        /// <returns>Byte array From startIndex to Length.</returns>
        public static byte[] Part(this byte[] sourceArray, int startIndex, int endIndex) {
            int newLength = endIndex - startIndex;
            byte[] Bytes = new byte[newLength];
            Buffer.BlockCopy(sourceArray, startIndex, Bytes, 0, newLength);
            return Bytes;
        }

        /// <summary>
        /// Byte Array equivalent of Substring.
        /// </summary>
        /// <param name="SourceArray"></param>
        /// <param name="startIndex"></param>
        /// <param name="Length"></param>
        /// <returns>Byte array From startIndex to Length.</returns>
        public static byte[] Part(this List<byte> sourceArray, int startIndex, int endIndex) {
            int newLength = endIndex - startIndex;
            byte[] Bytes = new byte[newLength];
            sourceArray.CopyTo(startIndex, Bytes, 0, newLength);
            //Buffer.BlockCopy(sourceArray, startIndex, Bytes, 0, newLength);
            return Bytes;
        }

        /// <summary>
        /// Searches for a byte array in the source array.
        /// </summary>
        /// <param name="sourceArray">Source byte array</param>
        /// <param name="byteArray">Search byte array</param>
        /// <returns></returns>
        public static int IndexOf(this List<byte> sourceArray, byte[] byteArray) {
            return sourceArray.IndexOf(byteArray, 0);
        }

        /// <summary>
        /// Searches for a byte array in the source array.
        /// </summary>
        /// <param name="sourceArray">Source byte array</param>
        /// <param name="byteArray">Search byte array</param>
        /// <returns></returns>
        public static int IndexOf(this byte[] sourceArray, byte[] byteArray) {
            return sourceArray.IndexOf(byteArray, 0);
        }

        /// <summary>
        /// <para>Searches for a byte in the source array.</para>
        /// <para>Array.IndexOf() Wrapper</para>
        /// </summary>
        /// <param name="sourceArray">Source byte</param>
        /// <param name="[byte]">Search byte</param>
        /// <returns></returns>
        public static int IndexOf(this byte[] sourceArray, byte @byte) {
            return Array.IndexOf(sourceArray, @byte);
        }

        public static int IndexOf(this List<byte> byteArray, byte[] subArray, int startIndex) {
            for (int i = startIndex, loopTo = byteArray.Count - subArray.Length; i <= loopTo; i++) {
                bool match = true;
                for (int j = 0, loopTo1 = subArray.Length - 1; j <= loopTo1; j++) {
                    if (byteArray[i + j] != subArray[j]) {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return i;
            }
            return -1;
        }

        public static int IndexOf(this byte[] byteArray, byte[] subArray, int startIndex) {
            for (int i = startIndex, loopTo = byteArray.Length - subArray.Length; i <= loopTo; i++) {
                bool match = true;
                for (int j = 0, loopTo1 = subArray.Length - 1; j <= loopTo1; j++) {
                    if (byteArray[i + j] != subArray[j]) {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return i;
            }
            return -1;
        }


        public static List<int> IndexOfAll(this List<byte> sourceArray, byte[] searchArray) {
            return sourceArray.IndexOfAll(searchArray, 0);
        }

        public static List<int> IndexOfAll(this List<byte> sourceArray, byte[] searchArray, int StartIndex) {
            if (sourceArray == null || searchArray is null)
                throw new ArgumentNullException("Source or subarray cannot be null.");
            if (searchArray.Length == 0 || searchArray.Length > sourceArray.Count)
                return new List<int>();

            int range = sourceArray.Count - searchArray.Length + 1;
            var results = new List<int>();

            for (int i = StartIndex; i < range; i++) {
                bool match = true;
                for (int j = 0; j < searchArray.Length; j++) {
                    if (sourceArray[i + j] != searchArray[j]) {
                        match = false;
                        break;
                    }
                }
                if (match)
                    results.Add(i);
            }

            return results;
        }

        public static List<int> IndexOfAll(this byte[] sourceArray, byte[] searchArray) {
            return sourceArray.IndexOfAll(searchArray, 0);
        }

        public static List<int> IndexOfAll(this byte[] sourceArray, byte[] searchArray, int StartIndex) {
            if (sourceArray == null || searchArray is null)
                throw new ArgumentNullException("Source or subarray cannot be null.");
            if (searchArray.Length == 0 || searchArray.Length > sourceArray.Length)
                return new List<int>();

            int range = sourceArray.Length - searchArray.Length + 1;
            var results = new List<int>();

            for (int i = StartIndex; i < range; i++) {
                bool match = true;
                for (int j = 0; j < searchArray.Length; j++) {
                    if (sourceArray[i + j] != searchArray[j]) {
                        match = false;
                        break;
                    }
                }
                if (match)
                    results.Add(i);
            }

            return results;
        }

        public static byte[] Concat(this byte[] A, byte[] B) {
            return Concat(new[] { A, B });
        }

        public static byte[] Concat(this byte[][] arrays) {
            return arrays.SelectMany(x => x).ToArray();
        }

        private const long OneKb = 1024L;
        private const long OneMb = OneKb * 1024L;
        private const long OneGb = OneMb * 1024L;
        private const long OneTb = OneGb * 1024L;

        public static string ByteToString(this int value, int decimalPlaces = 0) {
            return ((long)value).ByteToString(decimalPlaces);
        }

        public static string ByteToString(this long value, int decimalPlaces = 0) {
            string formatString = "{0:F" + decimalPlaces + "}"; // Format string for decimal places

            if (value >= OneTb) {
                return string.Format(formatString + " TB", value / (double)OneTb);
            } else if (value >= OneGb) {
                return string.Format(formatString + " GB", value / (double)OneGb);
            } else if (value >= OneMb) {
                return string.Format(formatString + " MB", value / (double)OneMb);
            } else if (value >= OneKb) {
                return string.Format(formatString + " KB", value / (double)OneKb);
            } else {
                return string.Format("{0} Bytes", value);
            }
        }
    }
}