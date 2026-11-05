# Voice dependency and redistribution review

NAudio's [MIT license](https://github.com/naudio/NAudio/blob/master/license.txt) permits commercial use and redistribution with its copyright/permission notice retained. That permission does not make its Windows audio backends portable across Unity targets. Release two therefore uses Unity's capture/playback APIs and managed Opus through Concentus, and **does not redistribute NAudio**.

[Concentus's license](https://github.com/lostromb/concentus/blob/master/LICENSE) permits binary redistribution subject to its notice/disclaimer and endorsement conditions. The complete notice is included in `SocketJack.Unity.Voice/THIRD-PARTY-NOTICES.md` and the Unity distribution. Only the managed codec is included. [Opus's official licensing page](https://opus-codec.org/license/) describes its permissive reference-code license and royalty-free patent grants, including their conditions. No Opus command-line tools, native accelerators or recording/transcription service are bundled.

The optional Web modules use the MIT System.Text.Json family. Their license and full upstream informational notices accompany the actual selected managed assemblies. Native SQLite is provided by the operating system; SQLitePCLRaw and Microsoft.Data.Sqlite are excluded from the Unity asset.

[Unity's submission guidelines](https://assetstore.unity.com/publishing/submission-guidelines), section 1.2, require dependent-license disclosure and included notices, and restrict incompatible dependent licenses. The listing identifies the optional codecs/JSON libraries; the artifact allowlist excludes unrelated/native packages. This is a review of the selected upstream terms and package contents, not a claim that Unity has approved the second release. Store review applies to the submitted artifact.

Developers/operators remain responsible for their own microphone permission flow, voice-data handling, account retention and deployed service terms. The supplied player starts capture only through explicit opt-in, and the service does not record audio or place it in chat history.
