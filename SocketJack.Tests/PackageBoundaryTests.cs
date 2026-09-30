using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SocketJack.Tests;

[TestClass]
public class PackageBoundaryTests
{
    [TestMethod]
    public void CoreEmbedsOnlyItsOwnPages()
    {
        var resources = typeof(SocketJack.Net.HttpServer).Assembly.GetManifestResourceNames();
        var pages = resources.Where(n => n.StartsWith("SocketJack.Html.")).ToArray();
        CollectionAssert.AreEquivalent(new[] {
            "SocketJack.Html.Admin.html", "SocketJack.Html.SqlLogin.html",
            "SocketJack.Html.SqlPanel.html", "SocketJack.Html.SocketJackHttpServerDefaultIndex.html"
        }, pages);
        Assert.IsTrue(SocketJack.HtmlPageResources.GetHtml("SocketJackHttpServerDefaultIndex.html").Length > 0);
        Assert.AreEqual("", SocketJack.HtmlPageResources.GetHtml("heirowLLMWebChat.html"));
    }
}
