using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace SocketJack.Tests;

[TestClass]
public class ApplicationResourceTests
{
    [TestMethod]
    public void ApplicationAssemblyOwnsChatAndEditorResources()
    {
        Assert.IsTrue(SocketJack.Net.HtmlPageResources.GetHtml("heirowLLMWebChat.html").Contains("<html"));
        Assert.IsTrue(SocketJack.Net.HtmlPageResources.GetHtml("heirowWebUI.html").Contains("<html"));
        Assert.IsTrue(SocketJack.Net.HtmlPageResources.GetBytes("heirowLLMFavicon.ico").Length > 0);
        Assert.AreNotEqual(typeof(HttpServer).Assembly, typeof(SocketJack.Net.HtmlPageResources).Assembly);
    }
}
