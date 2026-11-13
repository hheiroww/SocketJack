using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {
    public sealed partial class ApplicationWebSocket : IDisposable {
        private static ValueTask<T> Completed<T>(T result) => new ValueTask<T>(result);

        private const int MaximumRecordsPerAccount = 256;
        private const int MaximumRecordBytes = 16 * 1024;
        private const int MaximumMediaChunk = 64 * 1024;
        private const long MaximumMediaBytes = 256L * 1024 * 1024;
        private readonly FileStream storageLease;
        private HttpTransferStore mediaStore;

        private sealed class AccountData {
            public string DisplayName { get; set; } = "";
            public JsonElement? Preferences { get; set; }
            public Dictionary<string, List<Record>> Collections { get; set; } = new Dictionary<string, List<Record>>(StringComparer.Ordinal);
            public Dictionary<string, MediaEntry> Media { get; set; } = new Dictionary<string, MediaEntry>(StringComparer.Ordinal);
        }

        private sealed class Profile {
            public string DisplayName { get; set; }
            public JsonElement? Preferences { get; set; }
        }

        private sealed class Record {
            public string Id { get; set; }
            public long Revision { get; set; }
            public JsonElement Data { get; set; }
        }

        private sealed class RecordList {
            public List<Record> Records { get; set; }
        }

        private sealed class RecordQuery {
            public string Collection { get; set; }
            public string Id { get; set; }
            public long Revision { get; set; }
            public JsonElement Data { get; set; }
            public int? Limit { get; set; }
            public int Offset { get; set; }
        }

        private sealed class MediaEntry {
            public string Id { get; set; }
            public string Filename { get; set; }
            public string ContentType { get; set; }
            public string Sha256 { get; set; }
            public long Length { get; set; }
        }

        private sealed class MediaRequest {
            public string Id { get; set; }
            public string Filename { get; set; }
            public string ContentType { get; set; }
            public long Length { get; set; }
            public long Offset { get; set; }
            public string Sha256 { get; set; }
            public byte[] Data { get; set; }
            public int? MaximumBytes { get; set; }
        }

        private sealed class MediaProgress {
            public string Id { get; set; }
            public long Offset { get; set; }
            public bool Done { get; set; }
        }

        private sealed class MediaChunk {
            public string Id { get; set; }
            public long Offset { get; set; }
            public int ByteLength { get; set; }
            public long Length { get; set; }
            public bool Done { get; set; }
            public string ContentType { get; set; }
            public string Filename { get; set; }
            public string Sha256 { get; set; }
        }

        private sealed class MediaReadResult {
            internal MediaChunk Metadata;
            internal byte[] Bytes;
        }

        /// <summary>Registers a collection whose entries are validated with the browser JSON contract.</summary>
        public void RegisterCollection<T>(string name) {
            if (!IsName(name)) throw new ArgumentException("Invalid collection name.", nameof(name));
            lock (gate) collections.Add(name, typeof(T));
        }

        private void RegisterBuiltIns() {
            Register<object, Profile>("profile.get", (caller, _, _) => Completed(Account(caller, false, data => new Profile {
                DisplayName = data.DisplayName, Preferences = data.Preferences
            })));
            Register<Profile, Profile>("profile.update", (caller, input, _) => Completed(Account(caller, true, data => {
                if (input == null || input.DisplayName?.Length > 120 || (input.Preferences.HasValue && input.Preferences.Value.GetRawText().Length > 8192))
                    throw new ApplicationError("invalidInput", "Profile exceeds allowed limits.");
                if (input.DisplayName != null) data.DisplayName = input.DisplayName;
                if (input.Preferences.HasValue) data.Preferences = input.Preferences;
                return new Profile { DisplayName = data.DisplayName, Preferences = data.Preferences };
            })));
            Register<RecordQuery, RecordList>("records.list", (caller, input, _) => Completed(
                WithRecords(caller, input, false, (_, records, _) => {
                    if (input.Offset < 0 || input.Limit is < 1 or > 50)
                        throw new ApplicationError("invalidInput", "Invalid page.");
                    var page = new List<Record>();
                    int bytes = 0;
                    foreach (var record in records.Skip(input.Offset).Take(input.Limit ?? 20)) {
                        bytes += JsonSerializer.SerializeToUtf8Bytes(record, Json).Length;
                        if (bytes > MaximumMessageBytes / 2) break;
                        page.Add(record);
                    }
                    return new RecordList { Records = page };
                })));
            Register<RecordQuery, Record>("records.get", (caller, input, _) => Completed(
                WithRecords(caller, input, false, (_, records, _) => FindRecord(records, input.Id))));
            Register<RecordQuery, Record>("records.create", (caller, input, _) => Completed(
                WithRecords(caller, input, true, (account, records, type) => {
                    if (account.Collections.Values.Sum(items => items.Count) >= MaximumRecordsPerAccount)
                        throw new ApplicationError("quota", "Record quota reached.");
                    var record = new Record { Id = Guid.NewGuid().ToString("N"), Revision = 1, Data = ValidateRecord(input.Data, type) };
                    records.Add(record);
                    return record;
                })));
            Register<RecordQuery, Record>("records.update", (caller, input, _) => Completed(
                WithRecords(caller, input, true, (_, records, type) => {
                    var record = FindRecord(records, input.Id);
                    RequireRevision(record, input.Revision);
                    record.Data = ValidateRecord(input.Data, type);
                    record.Revision++;
                    return record;
                })));
            Register<RecordQuery, bool>("records.delete", (caller, input, _) => Completed(
                WithRecords(caller, input, true, (_, records, _) => {
                    var record = FindRecord(records, input.Id);
                    RequireRevision(record, input.Revision);
                    return records.Remove(record);
                })));
        }

        private T Account<T>(ApplicationCaller caller, bool write, Func<AccountData, T> action) {
            lock (gate) {
                EnsureCurrent(caller);
                string path = Path.Combine(root, Hash(caller.Subject) + ".json");
                var account = File.Exists(path) ? JsonSerializer.Deserialize<AccountData>(File.ReadAllBytes(path), Json) : new AccountData();
                T result = action(account);
                if (write) Save(path, account);
                return result;
            }
        }

        private void EnsureCurrent(ApplicationCaller caller) {
            if (!active(caller.Subject) || (caller.Session != null && GetSubject(caller.Session) != caller.Subject))
                throw new ApplicationError("unauthenticated", "Sign in required.");
        }

        private static void Save<T>(string path, T value) {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            string pending = path + ".pending";
            using (var file = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None)) {
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }
            if (File.Exists(path)) File.Replace(pending, path, null);
            else File.Move(pending, path);
        }

        private T WithRecords<T>(ApplicationCaller caller, RecordQuery input, bool write, Func<AccountData, List<Record>, Type, T> action) {
            return Account(caller, write, account => {
                if (input?.Collection == null || !collections.TryGetValue(input.Collection, out var type))
                    throw new ApplicationError("notFound", "Collection not found.");
                if (!account.Collections.TryGetValue(input.Collection, out var records)) {
                    records = new List<Record>();
                    if (write) account.Collections.Add(input.Collection, records);
                }
                return action(account, records, type);
            });
        }

        private static Record FindRecord(List<Record> records, string id) => records.Find(record => record.Id == id)
            ?? throw new ApplicationError("notFound", "Record not found.");

        private static void RequireRevision(Record record, long revision) {
            if (record.Revision != revision) throw new ApplicationError("conflict", "The record revision changed.");
        }

        private static JsonElement ValidateRecord(JsonElement input, Type type) {
            if (input.ValueKind != JsonValueKind.Object || input.GetRawText().Length > MaximumRecordBytes)
                throw new ApplicationError("invalidInput", "A bounded record object is required.");
            var value = input.Deserialize(type, RecordJson);
            var record = JsonSerializer.SerializeToElement(value, type, Json);
            if (record.GetRawText().Length > MaximumRecordBytes)
                throw new ApplicationError("tooLarge", "The record exceeds the allowed limit.");
            return record;
        }

        private static readonly JsonSerializerOptions RecordJson = new JsonSerializerOptions(Json) {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true
        };

        private void RegisterMedia() {
            mediaStore = new HttpTransferStore(new HttpTransferOptions {
                Directory = Path.Combine(root, "media"),
                MaxChunkBytes = MaximumMediaChunk
            });
            Register<MediaRequest, MediaProgress>("media.begin", (caller, input, _) => Completed(BeginMedia(caller, input)));
            Register<MediaRequest, MediaProgress>("media.append", (caller, input, _) => Completed(AppendMedia(caller, input)));
            Register<MediaRequest, MediaProgress>("media.appendBinary", (_, _, _) => throw new ApplicationError("invalidInput", "A binary upload frame is required."));
            Register<MediaRequest, MediaProgress>("media.status", (caller, input, _) => Completed(
                WithMedia(caller, input, entry => Progress(mediaStore.Get(Hash(caller.Subject), entry.Id)))));
            Register<MediaRequest, MediaProgress>("media.complete", (caller, input, _) => Completed(
                WithMedia(caller, input, entry => Progress(mediaStore.Complete(Hash(caller.Subject), entry.Id)))));
            Register<MediaRequest, MediaProgress>("media.cancel", (caller, input, _) => Completed(
                WithMedia(caller, input, entry => Progress(mediaStore.Cancel(Hash(caller.Subject), entry.Id, paused: true)))));
            Register<MediaRequest, MediaReadResult>("media.read", ReadMediaAsync);
            operations["media.read"].Output = typeof(MediaChunk);
        }

        private MediaProgress AppendMedia(ApplicationCaller caller, MediaRequest input) =>
            WithMedia(caller, input, entry => Progress(mediaStore.Append(Hash(caller.Subject), entry.Id, input.Offset, input.Data, input.Sha256)));

        private MediaProgress BeginMedia(ApplicationCaller caller, MediaRequest input) {
            if (input == null || string.IsNullOrWhiteSpace(input.Filename) || input.Filename.Length > 240 ||
                input.Filename.Any(c => char.IsControl(c) || c == '/' || c == '\\' || c == ':') ||
                input.Length < 0 || input.Length > MaximumMediaBytes || input.Sha256?.Length != 64 ||
                !input.Sha256.All(Uri.IsHexDigit))
                throw new ApplicationError("invalidInput", "A valid file name, size and hash are required.");
            string contentType = SafeMediaType(input.ContentType);
            return Account(caller, true, account => {
                string owner = Hash(caller.Subject);
                foreach (var old in account.Media.Values.ToArray()) {
                    var state = mediaStore.Get(owner, old.Id).State;
                    if (state == HttpTransferState.Expired || state == HttpTransferState.Failed)
                        account.Media.Remove(old.Id);
                }
                string target = Hash(input.Filename + "\n" + contentType + "\n" + input.Sha256.ToLowerInvariant());
                var existing = mediaStore.Find(owner, target);
                if (existing == null || existing.State == HttpTransferState.Expired || existing.State == HttpTransferState.Failed) {
                    if (account.Media.Count >= 32 || account.Media.Values.Sum(item => item.Length) + input.Length > MaximumMediaBytes * 2)
                        throw new ApplicationError("quota", "Media quota reached.");
                }
                var session = mediaStore.Create(owner, new HttpTransferCreateRequest {
                    Target = target, Length = input.Length, Sha256 = input.Sha256, Overwrite = HttpOverwritePolicy.Always
                });
                account.Media[session.Id] = new MediaEntry {
                    Id = session.Id, Filename = input.Filename, ContentType = contentType, Length = session.Length, Sha256 = session.Sha256
                };
                return Progress(session);
            });
        }

        private static string SafeMediaType(string value) {
            switch (value?.ToLowerInvariant()) {
                case "image/png": case "image/jpeg": case "image/gif": case "image/webp": case "image/avif":
                case "audio/mpeg": case "audio/wav": case "audio/ogg": case "audio/mp4": case "audio/webm":
                case "video/mp4": case "video/webm": case "video/ogg":
                    return value.ToLowerInvariant();
                default:
                    return "application/octet-stream";
            }
        }

        private T WithMedia<T>(ApplicationCaller caller, MediaRequest input, Func<MediaEntry, T> action) =>
            Account(caller, false, account => {
                if (input?.Id == null || !account.Media.TryGetValue(input.Id, out var entry))
                    throw new ApplicationError("notFound", "Media not found.");
                return action(entry);
            });

        private static MediaProgress Progress(HttpTransferSession session) =>
            new MediaProgress { Id = session.Id, Offset = session.Offset, Done = session.State == HttpTransferState.Completed };

        private async ValueTask<MediaReadResult> ReadMediaAsync(ApplicationCaller caller, MediaRequest input, CancellationToken cancellation) {
            var entry = WithMedia(caller, input, item => item);
            string owner = Hash(caller.Subject);
            var session = mediaStore.Get(owner, entry.Id);
            if (session.State != HttpTransferState.Completed) throw new ApplicationError("notFound", "Media not ready.");
            if (input.Offset < 0 || input.Offset > entry.Length || input.MaximumBytes is < 1 or > MaximumMediaChunk)
                throw new ApplicationError("invalidInput", "Invalid media range.");
            int count = (int)Math.Min(input.MaximumBytes ?? MaximumMediaChunk, entry.Length - input.Offset);
            using var output = new MemoryStream(count);
            if (count != 0)
                await mediaStore.CopyToAsync(owner, entry.Id, output, input.Offset, input.Offset + count - 1, cancellation).ConfigureAwait(false);
            byte[] bytes = output.ToArray();
            using var sha = SHA256.Create();
            return new MediaReadResult {
                Bytes = bytes,
                Metadata = new MediaChunk {
                    Id = entry.Id, Offset = input.Offset, ByteLength = bytes.Length, Length = entry.Length,
                    Done = input.Offset + bytes.Length == entry.Length, ContentType = entry.ContentType,
                    Filename = entry.Filename, Sha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
                }
            };
        }

        public void Dispose() {
            mediaStore?.Dispose();
            storageLease.Dispose();
        }
    }
}
