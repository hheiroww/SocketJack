Imports SocketJack.Net
Public Class HttpServerTest
    Implements ITest
    Private server As HttpServer
    Public ReadOnly Property TestName As String Implements ITest.TestName
        Get
            Return "HTTP Server"
        End Get
    End Property
    Public ReadOnly Property AutoStart As Boolean Implements ITest.AutoStart
        Get
            Return False
        End Get
    End Property
    Public Property Running As Boolean Implements ITest.Running
    Public Sub New()
        InitializeComponent()
    End Sub
    Public Sub StartTest() Implements ITest.StartTest
        If Running Then Return
        server = New HttpServer(12345)
        server.Map("GET", "/health", Function(connection, request, cancellation) "SocketJack is running")
        Running = server.Listen()
        ButtonStartStop.Content = If(Running, "Stop HTTP server", "Start HTTP server")
    End Sub
    Public Sub StopTest() Implements ITest.StopTest
        server?.Dispose()
        server = Nothing
        Running = False
        ButtonStartStop.Content = "Start HTTP server"
    End Sub
    Private Sub ToggleServer(sender As Object, e As RoutedEventArgs) Handles ButtonStartStop.Click
        If Running Then
            StopTest()
        Else
            StartTest()
        End If
    End Sub
    Private Sub StopOnUnload(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        StopTest()
    End Sub
End Class
