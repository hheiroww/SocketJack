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

    [TestMethod]
    public void AdminPageEventBindingsReferToExistingElements()
    {
        var html = SocketJack.HtmlPageResources.GetHtml("Admin.html");
        var ids = System.Text.RegularExpressions.Regex.Matches(html, "id=[\"']([^\"']+)[\"']")
            .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToHashSet();
        var bindings = System.Text.RegularExpressions.Regex.Matches(html, @"\$\('([^']+)'\)\.on\w+\s*=");
        Assert.IsTrue(bindings.Count > 0);
        foreach (System.Text.RegularExpressions.Match binding in bindings)
            Assert.IsTrue(ids.Contains(binding.Groups[1].Value), "Missing element for event handler: " + binding.Groups[1].Value);
    }
}
