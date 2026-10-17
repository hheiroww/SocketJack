using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Serialization;
using System.Text.Json;

namespace SocketJack.Tests;

[TestClass]
public sealed class BinaryObjectPieceTests {
    static JsonElement Object(int sequence,string stable) => JsonSerializer.SerializeToElement(new {
        sequence, changingPrefix=new string('x',sequence%97),
        nested=new { document="start-"+sequence+stable+"-end-"+sequence, progress=sequence/100.0 },
        anotherField=stable, changedSuffix=Guid.NewGuid().ToString()
    });
    static string Stable() { var bytes=new byte[16384];new Random(471).NextBytes(bytes);return Convert.ToBase64String(bytes); }
    static byte[] Deliver(BinaryCompressionSession from,BinaryCompressionSession to,byte[] frame,ref long controlsBytes) {
        var decoded=to.Decode(frame,out var replies);
        var controls=new Queue<(BinaryCompressionSession From,BinaryCompressionSession To,byte[] Frame)>();
        foreach(var reply in replies)controls.Enqueue((to,from,reply));
        int count=0;
        while(controls.Count!=0) {
            Assert.IsTrue(++count<1000,"Control exchange must terminate.");
            var item=controls.Dequeue();controlsBytes+=item.Frame.Length;
            Assert.IsNull(item.To.Decode(item.Frame,out var next));
            foreach(var reply in next)controls.Enqueue((item.To,item.From,reply));
        }
        return decoded;
    }
    [TestMethod]
    public void BinaryJsonFieldsRoundTripWithoutClrActivation() {
        var formatter=new BinarySerializer();
        using var input=JsonDocument.Parse("{\"Type\":\"System.Diagnostics.Process\",\"value\":{\"empty\":\"\",\"unicode\":\"こんにちは 🎵\",\"n\":-9223372036854775808,\"u\":18446744073709551615,\"fraction\":1.25,\"bool\":true,\"items\":[null,false,42]}}");
        byte[] raw=formatter.SerializeJson(input.RootElement);
        Assert.AreEqual(11,raw[3],"Object members are SB1 fields, not one opaque JSON string.");
        var decoded=formatter.DeserializeJson(raw);
        Assert.AreEqual("System.Diagnostics.Process",decoded.GetProperty("Type").GetString());
        var data=decoded.GetProperty("value");Assert.AreEqual(long.MinValue,data.GetProperty("n").GetInt64());
        Assert.AreEqual(ulong.MaxValue,data.GetProperty("u").GetUInt64());
        Assert.AreEqual("こんにちは 🎵",data.GetProperty("unicode").GetString());
        Assert.AreEqual(1.25,data.GetProperty("fraction").GetDouble());
        Assert.AreEqual(JsonValueKind.Null,data.GetProperty("items")[0].ValueKind);
    }
    [TestMethod]
    public void UniqueObjectsReuseInternalPiecesIncludingDefinitionTraffic() {
        var formatter=new BinarySerializer {ShareCompressionDictionary=false};string stable=Stable();
        using var sender=formatter.CreateCompressionSession();using var receiver=formatter.CreateCompressionSession();
        long originals=0,wire=0,controls=0;byte[] lastFrame=null;var seen=new HashSet<string>();
        for(int i=0;i<48;i++) {
            var value=Object(i,stable);Assert.IsTrue(seen.Add(value.GetRawText()),"Every complete object must differ.");
            var raw=formatter.SerializeJson(value);originals+=raw.Length;
            var frame=sender.Encode(raw,out var updates);wire+=frame.Length;
            foreach(var update in updates) { controls+=update.Length;Assert.IsNull(Deliver(sender,receiver,update,ref controls)); }
            CollectionAssert.AreEqual(raw,Deliver(sender,receiver,frame,ref controls));lastFrame=frame;
        }
        Assert.IsTrue(sender.DictionaryCount>0 && receiver.DictionaryCount>0);
        Assert.IsTrue(wire+controls<originals/3,$"Pieces should save bytes after all registrations/ACKs: {wire+controls}/{originals}.");
        Assert.IsTrue(lastFrame.Length<formatter.SerializeJson(Object(49,stable)).Length/4);
        using var unrelated=formatter.CreateCompressionSession();
        Assert.ThrowsException<InvalidDataException>(()=>unrelated.Decode(lastFrame,out _),"Private reference frames cannot decode with another client's dictionary.");
        Assert.IsTrue(formatter.CompressionMemoryUsed<=formatter.CompressionMemoryLimit);
    }
    [TestMethod]
    public void ReceivingRawChangingObjectsLearnsUploadPiecesAndPublishesDefinitions() {
        var formatter=new BinarySerializer();string stable=Stable();long controlBytes=0;
        using var browser=formatter.CreateCompressionSession();using var server=formatter.CreateCompressionSession();
        for(int i=0;i<12;i++) {
            var raw=formatter.SerializeJson(Object(i,stable));
            // Browser cold uploads have no definitions; server observes the raw SB1 pieces.
            CollectionAssert.AreEqual(raw,Deliver(browser,server,raw,ref controlBytes));
        }
        Assert.IsTrue(controlBytes>0 && browser.DictionaryCount>0,"Observed upload definitions must propagate back to the browser.");
        var changed=formatter.SerializeJson(Object(99,stable));var compressed=browser.Encode(changed,out var controls);
        Assert.IsTrue(compressed.Length<changed.Length/4,"Later uploads reference stable pieces inside changed objects.");
        foreach(var control in controls)Assert.IsNull(Deliver(browser,server,control,ref controlBytes));
        CollectionAssert.AreEqual(changed,Deliver(browser,server,compressed,ref controlBytes));
    }
    [TestMethod]
    public void ExternalSessionHonorsDisabledPropertyAndMessageLimit() {
        var formatter=new BinarySerializer {EnableCompression=false};using var session=formatter.CreateCompressionSession(1024);
        var raw=formatter.SerializeJson(JsonSerializer.SerializeToElement(new { text=new string('A',500) }));
        Assert.AreSame(raw,session.Encode(raw,out var controls));Assert.AreEqual(0,controls.Length);
        CollectionAssert.AreEqual(raw,session.Decode(raw,out controls));Assert.AreEqual(0,controls.Length);
        Assert.ThrowsException<InvalidDataException>(()=>session.Decode(new byte[1025],out _));
        Assert.ThrowsException<InvalidDataException>(()=>session.Encode(new byte[]{83,66,2,0},out _));
        session.Dispose();Assert.ThrowsException<ObjectDisposedException>(()=>session.Encode(raw,out _));
    }
}
