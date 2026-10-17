using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net.Database;

namespace SocketJack.Tests;

[TestClass]
public class IncrementalPersistenceTests {
    [TestMethod]
    public async Task ReloadedEncryptedStoreWritesOnlyChangedTables() {
        var root=Path.Combine(Path.GetTempPath(),"socketjack-incremental-"+Guid.NewGuid().ToString("N"));
        try {
            using(var writer=new DataServer(loadFromDisk:false){DataPath=root,AutoSave=false}) {
                writer.SetSqlAdminAccount("fixture","Fixture-Password!");
                writer.Databases["db"].OwnerUsername="fixture";
                var session=new SqlSession{Username="fixture",IsAuthenticated=true,CurrentDatabase="db"};
                writer.Execute(session,new SqlCommand("CREATE TABLE Jobs (Id int)"));
                writer.Execute(session,new SqlCommand("CREATE TABLE Stable (Id int)"));
                writer.Execute(session,new SqlCommand("INSERT INTO Jobs VALUES (1)"));
                writer.Execute(session,new SqlCommand("INSERT INTO Stable VALUES (10)"));
                await writer.SaveAsync();
            }
            var files=Directory.GetFiles(Path.Combine(root,"tables"),"*.json").ToDictionary(p=>p,File.ReadAllBytes);
            using(var writer=new DataServer(loadFromDisk:false){DataPath=root,AutoSave=false}) {
                await writer.LoadAsync(); await writer.SaveAsync();
                foreach(var file in files)CollectionAssert.AreEqual(file.Value,File.ReadAllBytes(file.Key),"Unchanged encrypted table was rewritten.");
                writer.Databases["db"].Tables["Jobs"].Rows.Add([2]);
                await writer.SaveAsync();
                foreach(var file in files.Where(f=>!Path.GetFileName(f.Key).StartsWith("db_Jobs_")))CollectionAssert.AreEqual(file.Value,File.ReadAllBytes(file.Key),"Unrelated table was rewritten.");
            }
            using var reader=new DataServer(loadFromDisk:false){DataPath=root,AutoSave=false};await reader.LoadAsync();
            Assert.AreEqual(2,reader.Databases["db"].Tables["Jobs"].Rows.Count);
            Assert.AreEqual(1,reader.Databases["db"].Tables["Stable"].Rows.Count);
        } finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    [TestMethod]
    public async Task ModeChangesAndModifiedFilesCannotReuseCachedHash() {
        var root=Path.Combine(Path.GetTempPath(),"socketjack-cache-check-"+Guid.NewGuid().ToString("N"));
        try {
            using var writer=new DataServer(loadFromDisk:false){DataPath=root,AutoSave=false};
            writer.SetSqlAdminAccount("fixture","Fixture-Password!");writer.Databases["db"].OwnerUsername="fixture";
            var session=new SqlSession{Username="fixture",IsAuthenticated=true,CurrentDatabase="db"};
            writer.Execute(session,new SqlCommand("CREATE TABLE Jobs (Id int)"));writer.Execute(session,new SqlCommand("INSERT INTO Jobs VALUES (1)"));await writer.SaveAsync();
            var file=Directory.GetFiles(Path.Combine(root,"tables"),"db_Jobs_*.json").Single();
            Assert.IsTrue(File.ReadAllText(file).Contains("\nSJEP1:"));
            writer.EnablePayloadEncryption=false;await writer.SaveAsync();Assert.IsFalse(File.ReadAllText(file).Contains("SJEP1:"));
            writer.EnablePayloadEncryption=true;await writer.SaveAsync();Assert.IsTrue(File.ReadAllText(file).Contains("\nSJEP1:"));
            File.WriteAllText(file,"corrupted file");await writer.SaveAsync();
            using var reader=new DataServer(loadFromDisk:false){DataPath=root,AutoSave=false};await reader.LoadAsync();Assert.AreEqual(1,reader.Databases["db"].Tables["Jobs"].Rows.Count);
        } finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
