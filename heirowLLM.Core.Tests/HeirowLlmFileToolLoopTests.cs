using heirowLLM;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using System.Reflection;
using System.Text.Json;

namespace SocketJack.Tests
{
    [TestClass]
    public sealed class HeirowLlmFileToolLoopTests
    {
        [TestMethod]
        public void CompletedRead_PreventsRepeatAndPromotesExistingFileWrite()
        {
            string dataRoot = Path.Combine(Path.GetTempPath(), "SocketJack.Tests", "file-tool-loop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);
            try
            {
                using var workstation = new HeirowLlm("127.0.0.1", 12341, 12342, 12343, dataRoot);
                string request = JsonSerializer.Serialize(new
                {
                    messages = new object[]
                    {
                        new { role = "system", content = "[heirowForge work mode]" },
                        new { role = "user", content = "Update index.html." },
                        new
                        {
                            role = "assistant",
                            tool_calls = new[]
                            {
                                new
                                {
                                    id = "call_read_index",
                                    type = "function",
                                    function = new { name = "vs_read_file", arguments = "{\"path\":\"\\\\index.html\"}" }
                                }
                            }
                        },
                        new { role = "tool", tool_call_id = "call_read_index", content = "```html index.html (Lines 1-2 of 2 total.)\n1: <!doctype html>\n2: </html>\n```" }
                    }
                });

                var repeatedReads = new List<ToolCallData>
                {
                    new ToolCallData { Id = "call_repeat", Name = "vs_read_file", ArgumentsJson = "{\"path\":\"index.html\"}" }
                };
                InvokeFileGuard(workstation, "SuppressRepeatedHeirowForgeDiscoveryCalls", repeatedReads, request);
                Assert.AreEqual(0, repeatedReads.Count, "An equivalent project path must not be read again in the same turn.");

                var writes = new List<ToolCallData>
                {
                    new ToolCallData { Id = "call_write", Name = "vs_write_file", ArgumentsJson = "{\"path\":\"index.html\",\"content\":\"updated\",\"overwrite\":false}" }
                };
                InvokeFileGuard(workstation, "PromoteInspectedVsWriteFileOverwrites", writes, request);

                using JsonDocument arguments = JsonDocument.Parse(writes[0].ArgumentsJson);
                Assert.IsTrue(arguments.RootElement.GetProperty("overwrite").GetBoolean(), "A successful read must authorize the intended write to the same existing file.");
            }
            finally
            {
                if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
            }
        }

        private static void InvokeFileGuard(HeirowLlm workstation, string methodName, List<ToolCallData> calls, string request)
        {
            MethodInfo method = typeof(HeirowLlm).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, methodName + " should exist.");
            method.Invoke(workstation, new object[] { calls, request, null, null });
        }
    }
}
