using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace SocketJack.Tests
{
    [TestClass]
    public sealed class SockJackDmlWorkflowServiceTests
    {
        [TestMethod]
        public void ProgressFileName_SanitizesProjectAndFeature()
        {
            string fileName = SockJackDmlWorkflowService.SanitizeProgressFileName("SocketJack", "SockJackDml Workflow Expansion");

            Assert.AreEqual("SocketJack_SockJackDml_Workflow_Expansion_Progress.md", fileName);
        }

        [TestMethod]
        public void ProgressDocument_CreateAndFind_PersistsRenderedMarkdown()
        {
            string root = CreateTempRoot();
            var service = new SockJackDmlWorkflowService(root);

            MagicProgressDocumentResult created = service.CreateOrUpdateProgress("owner", "session-a", new MagicProgressDocumentRequest
            {
                ProjectOrSessionName = "SocketJack",
                FeatureName = "SockJackDmlWorkflowExpansion",
                OverallPercent = 35,
                CurrentStatus = "Workflow storage and tracker rendering implemented.",
                LogEntry = "Created focused workflow progress tracker.",
                Stages = new List<MagicProgressStage>
                {
                    new MagicProgressStage { Name = "Workflow status", Status = "in_progress", Percent = 50, Notes = "Snapshots render from durable state." }
                },
                NextStep = "Wire endpoints and GUI controls.",
                VerificationNotes = "Unit coverage pending."
            });

            MagicProgressFindResult found = service.FindProgressDocument("owner", "session-a", new MagicProgressFindRequest
            {
                ProjectOrSessionName = "SocketJack",
                FeatureName = "SockJackDmlWorkflowExpansion"
            });

            Assert.IsTrue(created.Ok);
            Assert.AreEqual("SocketJack_SockJackDmlWorkflowExpansion_Progress.md", created.ProgressFileName);
            StringAssert.Contains(created.Markdown, "Workflow storage and tracker rendering implemented.");
            Assert.IsTrue(found.Ok);
            Assert.AreEqual(created.ProgressId, found.Progress.ProgressId);
        }

        [TestMethod]
        public async Task PlanExecute_InvalidWriteAction_ReturnsValidationAndPreviewPacket()
        {
            string root = CreateTempRoot();
            var service = new SockJackDmlWorkflowService(root);

            MagicPlanExecutionResult result = await service.ExecutePlanAsync(
                "owner",
                "session-a",
                new MagicPlanExecutionRequest
                {
                    ProjectOrSessionName = "SocketJack",
                    FeatureName = "SockJackDmlWorkflowExpansion",
                    ExecutionMode = "preview",
                    Actions = new List<MagicPlanExecutionAction>
                    {
                        new MagicPlanExecutionAction { Id = "write-progress", Type = "write_file", Path = "SocketJack_SockJackDmlWorkflowExpansion_Progress.md" }
                    }
                },
                action => Task.FromResult(new MagicPlanActionResult { ActionId = action.Id, Status = "completed", Output = "should not run" }));

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("invalid", result.Status);
            Assert.IsNotNull(result.Validation);
            Assert.IsFalse(result.Validation.Ok);
            Assert.IsTrue(result.Validation.Errors.Any(error => error.Field == "content"));
            Assert.IsNotNull(result.Preview);
            CollectionAssert.Contains(result.Preview.FilesToWrite, "SocketJack_SockJackDmlWorkflowExpansion_Progress.md");
        }

        [TestMethod]
        public async Task WorkflowStatus_ControlAndEvidence_AggregatesLatestState()
        {
            string root = CreateTempRoot();
            var service = new SockJackDmlWorkflowService(root);

            MagicPlanExecutionResult execution = await service.ExecutePlanAsync(
                "owner",
                "session-a",
                new MagicPlanExecutionRequest
                {
                    ProjectOrSessionName = "SocketJack",
                    FeatureName = "SockJackDmlWorkflowExpansion",
                    ExecutionMode = "preview",
                    Actions = new List<MagicPlanExecutionAction>
                    {
                        new MagicPlanExecutionAction { Id = "note-1", Type = "progress", Summary = "Preview workflow state." }
                    }
                },
                action => Task.FromResult(new MagicPlanActionResult { ActionId = action.Id, Status = "completed", Output = "preview" }));

            MagicExecutionControlResult paused = service.ControlExecution("owner", "session-a", new MagicExecutionControlRequest
            {
                ExecutionId = execution.ExecutionId,
                Action = "pause",
                Reason = "Waiting on approval."
            });
            MagicEvidenceLinkResult link = service.LinkEvidence("owner", "session-a", new MagicEvidenceLinkRequest
            {
                EvidencePacketId = "packet_1",
                ExecutionId = execution.ExecutionId,
                ActionId = "note-1",
                Summary = "Preview evidence link.",
                SourceKind = "verification"
            });
            MagicWorkflowStatusResult status = service.GetWorkflowStatus("owner", "session-a", new MagicWorkflowStatusRequest());

            Assert.IsTrue(paused.Ok);
            Assert.AreEqual("paused", paused.Status);
            Assert.IsTrue(link.Ok);
            Assert.AreEqual(1, status.ExecutionCount);
            Assert.AreEqual(1, status.EvidenceLinkCount);
            Assert.AreEqual("paused", status.LatestStatus);
            Assert.AreEqual("Waiting on approval.", status.LatestBlocker);
        }

        private static string CreateTempRoot()
        {
            string path = Path.Combine(Path.GetTempPath(), "SocketJack.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
