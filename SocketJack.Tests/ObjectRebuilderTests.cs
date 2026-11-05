using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace SocketJack.Tests
{
    [TestClass]
    public class ObjectRebuilderTests
    {
        [TestMethod]
        public void SharedRebuilderPreservesStrictLegacyDuplicatesAndConnectionIsolation()
        {
            var first = new ObjectRebuilder(); var second = new ObjectRebuilder();
            Assert.IsNull(first.Add("same", 2, 0, new byte[] { 1 }, 100));
            Assert.IsNull(second.Add("same", 2, 1, new byte[] { 9 }, 100));
            Assert.ThrowsException<InvalidDataException>(() => first.Add("same", 2, 0, new byte[] { 1 }, 100));
            CollectionAssert.AreEqual(new byte[] { 1, 2 }, first.Add("same", 2, 1, new byte[] { 2 }, 100));
            CollectionAssert.AreEqual(new byte[] { 8, 9 }, second.Add("same", 2, 0, new byte[] { 8 }, 100));
        }

        [TestMethod]
        public void SharedRebuilderBoundsAllInflightObjectsAndExpiresUsingMonotonicTime()
        {
            double now = 0; var buffer = new ObjectRebuilder(1, 10, 1, clock: () => now);
            Assert.IsNull(buffer.Add("one", 2, 0, new byte[] { 1, 2 }, 3));
            Assert.ThrowsException<InvalidDataException>(() => buffer.Add("two", 2, 0, new byte[] { 1 }, 3));
            Assert.ThrowsException<InvalidDataException>(() => buffer.Add("one", 2, 1, new byte[] { 3, 4 }, 3));
            now = 2; buffer.Prune(); Assert.AreEqual(0L, buffer.BufferedBytes);
            CollectionAssert.AreEqual(new byte[] { 1 }, buffer.Add("two", 1, 0, new byte[] { 1 }, 3));
        }

        [TestMethod]
        public void SharedRebuilderPrunesOnlyAfterEarliestExpiryAndClearResetsDeadline()
        {
            double now = 0; var buffer = new ObjectRebuilder(timeoutSeconds: 1, clock: () => now);
            buffer.Prune(); Assert.AreEqual(0, buffer.Count);
            buffer.Add("first", 2, 0, new byte[] { 1 }, 100);
            now = .5; buffer.Add("second", 2, 0, new byte[] { 2 }, 100);
            now = 1; buffer.Prune(); Assert.AreEqual(2, buffer.Count);
            now = 1.1; buffer.Prune(); Assert.AreEqual(1, buffer.Count); Assert.AreEqual(1L, buffer.BufferedBytes);
            buffer.Clear(); now = 20; buffer.Prune(); Assert.AreEqual(0, buffer.Count);
            buffer.Add("third", 2, 0, new byte[] { 3 }, 100);
            now = 21; buffer.Prune(); Assert.AreEqual(1, buffer.Count);
            now = 21.1; buffer.Prune(); Assert.AreEqual(0, buffer.Count);
        }
    }
}
