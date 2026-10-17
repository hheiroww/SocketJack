using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net.Database;

namespace SocketJack.Tests;

[TestClass]
public class DurablePersistenceRecoveryTests {
    [TestMethod]
    public async Task InterruptedSplitSaveRecoversOnlyCommittedTableGeneration() {
        var root=Path.Combine(Path.GetTempPath(),"socketjack-interrupted-"+Guid.NewGuid().ToString("N"));
        try {
            using var writer=new DataServer(loadFromDisk:false) {DataPath=root,AutoSave=false};
            writer.SetSqlAdminAccount("fixture","Fixture-Password!");
            writer.Databases["db"].OwnerUsername="fixture";
            var session=new SqlSession {Username="fixture",IsAuthenticated=true,CurrentDatabase="db"};
            writer.Execute(session,new SqlCommand("CREATE TABLE Jobs (Id int)"));
            writer.Execute(session,new SqlCommand("INSERT INTO Jobs VALUES (1)"));
            await writer.SaveAsync();
            var manifest=File.ReadAllBytes(Path.Combine(root,"_manifest.json"));
            writer.Execute(session,new SqlCommand("INSERT INTO Jobs VALUES (2)"));
            await writer.SaveAsync();
            // Crash after table replacements but before the second manifest commit.
            File.WriteAllBytes(Path.Combine(root,"_manifest.json"),manifest);
            using(var reader=new DataServer(loadFromDisk:false) {DataPath=root,AutoSave=false}) {
                await reader.LoadAsync();
                Assert.AreEqual(1,reader.Databases["db"].Tables["Jobs"].Rows.Count);
            }
            // A self-consistent backup from a different generation must remain rejected.
            var table=Directory.GetFiles(Path.Combine(root,"tables"),"db_Jobs_*.json").Single();
            File.Copy(table,table+".bak",true);
            using var invalid=new DataServer(loadFromDisk:false) {DataPath=root,AutoSave=false};
            await Assert.ThrowsExceptionAsync<IOException>(()=>invalid.LoadAsync());
        } finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
}
