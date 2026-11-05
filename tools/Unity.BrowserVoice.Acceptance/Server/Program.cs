using System.Net;
using System.Text.Json;
using SocketJack.Multiplayer.Web;
using SocketJack.Multiplayer.Voice;
using SocketJack.Multiplayer.Voice.Web;

if (args.Length != 6) throw new ArgumentException("Supply API port, allowed page origin, ready path, stop path, receipt path and owned database directory.");
int port = int.Parse(args[0]); string origin = args[1], ready = args[2], stop = args[3], receipt = args[4], directory = args[5];
Directory.CreateDirectory(directory);
using var store = new SqliteAccountStore(Path.Combine(directory, "accounts.sqlite"));
await using var server = new SocketJackWebServer(store, store, new SocketJackWebOptions { ListenEndPoint = new IPEndPoint(IPAddress.Loopback, port), AllowedOrigins = new[] { origin } });
using var relay = new AccountVoiceRelay(server); server.Start();
using var nativeAccount = new SocketJackWebClient(new Uri("http://127.0.0.1:" + port));
await nativeAccount.RegisterAsync("native_receiver", Guid.NewGuid().ToString("N") + "aA7!", "Native receiver");
await nativeAccount.StartRealtimeAsync();
using var nativeVoice = new AccountVoiceClient(nativeAccount); await nativeVoice.ConnectAsync();
var decoder = new OpusVoiceCodec(); var encoder = new OpusVoiceCodec(); var output = new float[320];
int frames = 0, chats = 0; double energy = 0;
nativeVoice.FrameReceived += frame => { decoder.Decode(frame.EncodedAudio, output); foreach (float sample in output) energy += sample * sample; Interlocked.Increment(ref frames); };
nativeAccount.ChatMessageReceived += message => { if (message.Text == "browser-text-probe") { Interlocked.Increment(ref chats); _ = nativeAccount.SendChatAsync("global", "native-text-reply"); } };
File.WriteAllText(ready + ".writing", "ready"); File.Move(ready + ".writing", ready);
var until = DateTime.UtcNow.AddMinutes(5); int sent = 0; var pcm = new float[320];
while (!File.Exists(stop) && DateTime.UtcNow < until)
{
    for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(.2 * Math.Sin(2 * Math.PI * 440 * (sent * 320 + i) / 16000));
    if (nativeVoice.SendEncoded(encoder.Encode(pcm), VoiceChannel.Global)) sent++;
    await Task.Delay(20);
}
await nativeVoice.StopAsync(); await nativeAccount.LogoutAsync(); await server.StopAsync();
bool passed = frames >= 50 && chats >= 1 && energy > 1;
File.WriteAllText(receipt, JsonSerializer.Serialize(new { result = passed ? "PASS" : "FAIL", receivedFrames = frames, textChats = chats, decodedEnergy = energy, generatedFrames = sent,
    forwardedFrames = relay.ForwardedFrames, syntheticAudioOnly = true, realMicrophoneUsed = false }, new JsonSerializerOptions { WriteIndented = true }));
return passed ? 0 : 1;
