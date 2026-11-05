using System;

namespace SocketJack.Multiplayer.Web
{
    public sealed class WebAccount
    {
        public string Id { get; set; }
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string Role { get; set; } = "player";
    }
    public sealed class WebAccountSession
    {
        public string Token { get; set; }
        public WebAccount User { get; set; }
    }
    public sealed class WebAccountException : InvalidOperationException
    {
        public int StatusCode { get; }
        public WebAccountException(string message, int statusCode = 400) : base(message) { StatusCode = statusCode; }
    }
}
