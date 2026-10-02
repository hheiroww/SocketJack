Imports System.Collections.ObjectModel
Imports System.ComponentModel
Imports System.Diagnostics
Imports System.IO
Imports System.Net.Sockets
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports System.Windows.Threading
Imports SocketJack.Extensions
Imports SocketJack.Net
Imports SocketJack.Serialization
Imports SocketJack.Compression

''' <summary>
''' Comprehensive test control for <see cref="MutableTcpServer"/> that exercises all
''' protocol handlers (HTTP, SocketJack, WebSocket, BroadcastServer) on a single socket
''' and reports pass/fail for each feature.
''' </summary>
Public Class MutableTcpServerTest
    Implements ITest

    Private Const ServerPort As Integer = 21070
    Private Const TimeoutMs As Integer = 5000
    Private Server As MutableTcpServer
    Private Broadcast As BroadcastServer
    Private _cts As CancellationTokenSource
    Private _tempDir As String
    Private _runningTask As Task
    Private ReadOnly _testGate As New SemaphoreSlim(1, 1)

    Public ReadOnly Property TestResults As New ObservableCollection(Of TestResult)

#Region "Test Result Model"

    Public Class TestResult
        Implements INotifyPropertyChanged

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Private Sub Notify(prop As String)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(prop))
        End Sub

        Private _status As String = "..."
        Private _name As String
        Private _duration As String = ""
        Private _error As String = ""
        Private _bytes As String = ""
        Private _rating As String = ""
        Private _resultText As String = ""
        Private _compute As String = ""
        Private _memory As String = ""
        Private _elapsedMs As Long = 0
        Private _byteCount As Long = 0
        Private _ratingValue As Double = 0
        Private _computePoints As Double = 0
        Private _memoryDeltaBytes As Long = 0
        Private _cpuPercent As Double = 0

        Public Property Status As String
            Get
                Return _status
            End Get
            Set(value As String)
                _status = value
                Notify(NameOf(Status))
            End Set
        End Property

        Public Property Name As String
            Get
                Return _name
            End Get
            Set(value As String)
                _name = value
                Notify(NameOf(Name))
            End Set
        End Property

        Public Property Duration As String
            Get
                Return _duration
            End Get
            Set(value As String)
                _duration = value
                Notify(NameOf(Duration))
            End Set
        End Property

        Public Property [Error] As String
            Get
                Return _error
            End Get
            Set(value As String)
                _error = value
                Notify(NameOf([Error]))
            End Set
        End Property

        Public Property Bytes As String
            Get
                Return _bytes
            End Get
            Set(value As String)
                _bytes = value
                Notify(NameOf(Bytes))
            End Set
        End Property

        Public Property Rating As String
            Get
                Return _rating
            End Get
            Set(value As String)
                _rating = value
                Notify(NameOf(Rating))
            End Set
        End Property

        Public Property ResultText As String
            Get
                Return _resultText
            End Get
            Set(value As String)
                _resultText = value
                Notify(NameOf(ResultText))
            End Set
        End Property

        Public Property ElapsedMs As Long
            Get
                Return _elapsedMs
            End Get
            Set(value As Long)
                _elapsedMs = value
                Notify(NameOf(ElapsedMs))
            End Set
        End Property

        Public Property ByteCount As Long
            Get
                Return _byteCount
            End Get
            Set(value As Long)
                _byteCount = value
                Notify(NameOf(ByteCount))
            End Set
        End Property

        Public Property RatingValue As Double
            Get
                Return _ratingValue
            End Get
            Set(value As Double)
                _ratingValue = value
                Notify(NameOf(RatingValue))
            End Set
        End Property

        Public Property Compute As String
            Get
                Return _compute
            End Get
            Set(value As String)
                _compute = value
                Notify(NameOf(Compute))
            End Set
        End Property

        Public Property Memory As String
            Get
                Return _memory
            End Get
            Set(value As String)
                _memory = value
                Notify(NameOf(Memory))
            End Set
        End Property

        Public Property ComputePoints As Double
            Get
                Return _computePoints
            End Get
            Set(value As Double)
                _computePoints = value
            End Set
        End Property

        Private _cpuTimeMs As Double = 0
        Public Property CpuTimeMs As Double
            Get
                Return _cpuTimeMs
            End Get
            Set(value As Double)
                _cpuTimeMs = value
            End Set
        End Property

        Public Property MemoryDeltaBytes As Long
            Get
                Return _memoryDeltaBytes
            End Get
            Set(value As Long)
                _memoryDeltaBytes = value
                Notify(NameOf(MemoryDeltaBytes))
            End Set
        End Property

        Public Property CpuPercent As Double
            Get
                Return _cpuPercent
            End Get
            Set(value As Double)
                _cpuPercent = value
                Notify(NameOf(CpuPercent))
            End Set
        End Property

        Private _isSelected As Boolean = True
        Private _category As String = ""
        Private _cycles As String = ""
        Private _cycleCount As Integer = 0

        Public Property IsSelected As Boolean
            Get
                Return _isSelected
            End Get
            Set(value As Boolean)
                _isSelected = value
                Notify(NameOf(IsSelected))
            End Set
        End Property

        Public Property Category As String
            Get
                Return _category
            End Get
            Set(value As String)
                _category = value
                Notify(NameOf(Category))
            End Set
        End Property

        Public Property Cycles As String
            Get
                Return _cycles
            End Get
            Set(value As String)
                _cycles = value
                Notify(NameOf(Cycles))
            End Set
        End Property

        Public Property CycleCount As Integer
            Get
                Return _cycleCount
            End Get
            Set(value As Integer)
                _cycleCount = value
                Notify(NameOf(CycleCount))
            End Set
        End Property
    End Class

#End Region

#Region "Test Message"

    Public Class MutableTestMessage
        Public Property Text As String = ""
        Public Property Number As Integer = 0
    End Class

#End Region

    ''' <summary>Maps column header text to the numeric backing property for sort.</summary>
    Private Shared ReadOnly SortPropertyMap As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
        {"Status", "Status"}, {"Test", "Name"},
        {"Time", "ElapsedMs"}, {"Cycles", "CycleCount"},
        {"I/O", "ByteCount"}, {"CPU %", "CpuPercent"},
        {"Memory", "MemoryDeltaBytes"}, {"Rating", "RatingValue"},
        {"Result", "ResultText"}
    }

    Private _currentSortProperty As String = Nothing
    Private _currentSortHeader As String = Nothing
    Private _sortClickCount As Integer = 0

    Public Sub New()
        InitializeComponent()
        TestResultsList.ItemsSource = TestResults
        PopulateTests()
        ' Prevent Server_Listen from ever being unchecked via its row checkbox
        Dim listen = TestResults.FirstOrDefault(Function(r) r.Name = "Server_Listen")
        If listen IsNot Nothing Then
            AddHandler listen.PropertyChanged,
                Sub(s, ev)
                    If _updatingSelectAll Then Return
                    If ev.PropertyName = NameOf(TestResult.IsSelected) AndAlso Not DirectCast(s, TestResult).IsSelected Then
                        DirectCast(s, TestResult).IsSelected = True
                    End If
                End Sub
        End If
        AddHandler CheckSelectAll.Checked, AddressOf CheckSelectAll_Changed
        AddHandler CheckSelectAll.Unchecked, AddressOf CheckSelectAll_Changed
        Dim catPanel = DirectCast(CatProperties.Parent, WrapPanel)
        For Each child In catPanel.Children
            If TypeOf child Is CheckBox Then
                Dim chk = DirectCast(child, CheckBox)
                AddHandler chk.Checked, AddressOf CategoryCheckBox_Changed
                AddHandler chk.Unchecked, AddressOf CategoryCheckBox_Changed
            End If
        Next
    End Sub

    Private Sub ColumnHeader_Click(sender As Object, e As RoutedEventArgs)
        Dim header = TryCast(e.OriginalSource, GridViewColumnHeader)
        If header Is Nothing OrElse header.Role = GridViewColumnHeaderRole.Padding Then Return
        Dim headerText = TryCast(header.Content, String)
        If headerText Is Nothing Then Return

        Dim sortProp As String = Nothing
        If Not SortPropertyMap.TryGetValue(headerText, sortProp) Then Return

        Dim view = CollectionViewSource.GetDefaultView(TestResultsList.ItemsSource)
        If view Is Nothing Then Return

        If _currentSortHeader = headerText Then
            ' Same column clicked again
            _sortClickCount += 1
            If _sortClickCount = 2 Then
                ' Second click → descending (max to min)
                view.SortDescriptions.Clear()
                view.SortDescriptions.Add(New SortDescription(sortProp, ComponentModel.ListSortDirection.Descending))
                LogSortedResults(headerText, "descending")
            Else
                ' Third click → reset to default order
                view.SortDescriptions.Clear()
                _currentSortHeader = Nothing
                _currentSortProperty = Nothing
                _sortClickCount = 0
                LogSortedResults(Nothing, Nothing)
            End If
        Else
            ' Different column → ascending (min to max)
            view.SortDescriptions.Clear()
            view.SortDescriptions.Add(New SortDescription(sortProp, ComponentModel.ListSortDirection.Ascending))
            _currentSortHeader = headerText
            _currentSortProperty = sortProp
            _sortClickCount = 1
            LogSortedResults(headerText, "ascending")
        End If
    End Sub

    ''' <summary>Outputs the current sorted test results table to the log.</summary>
    Private Sub LogSortedResults(sortColumn As String, direction As String)
        Dim view = CollectionViewSource.GetDefaultView(TestResultsList.ItemsSource)
        If view Is Nothing Then Return

        Dim items = view.Cast(Of TestResult)().Where(Function(r) r.Status = "PASS" OrElse r.Status = "FAIL").ToList()
        If items.Count = 0 Then Return

        Log("")
        If sortColumn IsNot Nothing Then
            Log("[Test] -- Sorted by: " & sortColumn & " (" & direction & ") --")
        Else
            Log("[Test] -- Sort reset to default order --")
        End If
        Log(String.Format("[Test]  {0}  {1}  {2}  {3}  {4}  {5}  {6}  {7}  {8}",
                          "Status".PadRight(6), "Test".PadRight(28),
                          "Time".PadLeft(8), "Cycles".PadLeft(7),
                          "I/O".PadLeft(10), "CPU %".PadLeft(7),
                          "Memory".PadLeft(10), "Rating".PadLeft(6),
                          "Result".PadLeft(6)))
        Log("[Test]  " & New String("-", 96))
        For Each r In items
            Log(String.Format("[Test]  {0}  {1}  {2}  {3}  {4}  {5}  {6}  {7}  {8}",
                              r.Status.PadRight(6),
                              r.Name.PadRight(28),
                              FormatElapsed(r.ElapsedMs).PadLeft(8),
                              r.CycleCount.ToString().PadLeft(7),
                              FormatBytes(r.ByteCount).PadLeft(10),
                              r.CpuPercent.ToString("N0").PadLeft(6) & "%",
                              FormatMemory(r.MemoryDeltaBytes).PadLeft(10),
                              r.RatingValue.ToString("N1").PadLeft(6),
                              r.ResultText.PadLeft(6)))
        Next
        Log("[Test]  " & New String("-", 96))
    End Sub

    Private Sub PopulateTests()
        TestResults.Add(New TestResult With {.Category = "Properties", .Name = "Property_Http"})
        TestResults.Add(New TestResult With {.Category = "Properties", .Name = "Property_SocketJack"})
        TestResults.Add(New TestResult With {.Category = "Properties", .Name = "Property_WebSocket"})
        TestResults.Add(New TestResult With {.Category = "Registration", .Name = "RegisterProtocol_Custom"})
        TestResults.Add(New TestResult With {.Category = "Registration", .Name = "RemoveProtocol"})
        TestResults.Add(New TestResult With {.Category = "Lifecycle", .Name = "Server_Listen"})
        TestResults.Add(New TestResult With {.Category = "HTTP", .Name = "Http_IndexPage"})
        TestResults.Add(New TestResult With {.Category = "HTTP", .Name = "Http_Robots"})
        TestResults.Add(New TestResult With {.Category = "HTTP", .Name = "Http_MapGet"})
        TestResults.Add(New TestResult With {.Category = "HTTP", .Name = "Http_MapPost"})
        TestResults.Add(New TestResult With {.Category = "HTTP", .Name = "Http_RemoveRoute"})
        TestResults.Add(New TestResult With {.Category = "HTTP", .Name = "Http_MapDirectory"})
        TestResults.Add(New TestResult With {.Category = "HTTP", .Name = "Http_DirectoryListing"})
        TestResults.Add(New TestResult With {.Category = "HTTP", .Name = "Http_MapStream"})
        TestResults.Add(New TestResult With {.Category = "HTTP", .Name = "Http_MapUploadStream"})
        TestResults.Add(New TestResult With {.Category = "HTTP", .Name = "Http_HeadRequest"})
        TestResults.Add(New TestResult With {.Category = "HTTP", .Name = "Http_OnHttpRequest"})
        TestResults.Add(New TestResult With {.Category = "HTTP", .Name = "Http_ChunkedSettings"})
        TestResults.Add(New TestResult With {.Category = "SocketJack", .Name = "SJ_Connect"})
        TestResults.Add(New TestResult With {.Category = "SocketJack", .Name = "SJ_SendReceive"})
        TestResults.Add(New TestResult With {.Category = "SocketJack", .Name = "SJ_ClientConnectedEvent"})
        TestResults.Add(New TestResult With {.Category = "SocketJack", .Name = "SJ_Broadcast"})
        TestResults.Add(New TestResult With {.Category = "SocketJack", .Name = "SJ_BroadcastExcept"})
        TestResults.Add(New TestResult With {.Category = "WebSocket", .Name = "WS_Handshake_101"})
        TestResults.Add(New TestResult With {.Category = "WebSocket", .Name = "WS_ClientConnectedEvent"})
        TestResults.Add(New TestResult With {.Category = "WebSocket", .Name = "WS_SendReceive"})
        TestResults.Add(New TestResult With {.Category = "WebSocket", .Name = "WS_PingPong"})
        TestResults.Add(New TestResult With {.Category = "WebSocket", .Name = "WS_CloseFrame"})
        TestResults.Add(New TestResult With {.Category = "Broadcast", .Name = "Broadcast_Register"})
        TestResults.Add(New TestResult With {.Category = "Broadcast", .Name = "Broadcast_StreamActive"})
        TestResults.Add(New TestResult With {.Category = "Cross-Protocol", .Name = "Cross_HttpAndSocketJack"})
        TestResults.Add(New TestResult With {.Category = "Cross-Protocol", .Name = "Cross_HttpAndWebSocket"})
        TestResults.Add(New TestResult With {.Category = "Cross-Protocol", .Name = "Cross_BroadcastSkipsHttp"})
        TestResults.Add(New TestResult With {.Category = "Dispose", .Name = "Dispose_NoThrow"})
        TestResults.Add(New TestResult With {.Category = "Serialization", .Name = "Ser_RoundTrip"})
        TestResults.Add(New TestResult With {.Category = "Serialization", .Name = "Ser_LargeObject"})
        TestResults.Add(New TestResult With {.Category = "Serialization", .Name = "Ser_TypePreservation"})
        TestResults.Add(New TestResult With {.Category = "Compression", .Name = "Cmp_Deflate"})
        TestResults.Add(New TestResult With {.Category = "Compression", .Name = "Cmp_GZip"})
        TestResults.Add(New TestResult With {.Category = "Compression", .Name = "Cmp_RoundTrip"})
    End Sub

    Private _updatingSelectAll As Boolean = False

    Private Sub CheckSelectAll_Changed(sender As Object, e As RoutedEventArgs)
        If _updatingSelectAll Then Return
        _updatingSelectAll = True
        Dim selected = CheckSelectAll.IsChecked = True
        For Each r In TestResults
            r.IsSelected = selected
        Next
        ' Server_Listen must always remain selected
        Dim listen = TestResults.FirstOrDefault(Function(r) r.Name = "Server_Listen")
        If listen IsNot Nothing Then listen.IsSelected = True
        Dim catPanel = DirectCast(CatProperties.Parent, WrapPanel)
        For Each child In catPanel.Children
            If TypeOf child Is CheckBox Then DirectCast(child, CheckBox).IsChecked = selected
        Next
        ' Lifecycle must stay checked since Server_Listen is always selected
        If Not selected Then CatLifecycle.IsChecked = True
        _updatingSelectAll = False
    End Sub

    Private Sub CategoryCheckBox_Changed(sender As Object, e As RoutedEventArgs)
        If _updatingSelectAll Then Return
        Dim chk = DirectCast(sender, CheckBox)
        Dim category = CStr(chk.Tag)
        Dim selected = chk.IsChecked = True
        For Each r In TestResults
            If r.Category = category Then r.IsSelected = selected
        Next
        ' Server_Listen must always remain selected
        Dim listen = TestResults.FirstOrDefault(Function(r) r.Name = "Server_Listen")
        If listen IsNot Nothing Then listen.IsSelected = True
        ' Keep the Lifecycle checkbox checked since Server_Listen is always on
        If category.Equals("Lifecycle", StringComparison.OrdinalIgnoreCase) AndAlso Not selected Then
            _updatingSelectAll = True
            CatLifecycle.IsChecked = True
            _updatingSelectAll = False
        End If
    End Sub

#Region "ITest Implementation"

    Public ReadOnly Property TestName As String Implements ITest.TestName
        Get
            Return "MutableTcpServer"
        End Get
    End Property

    Public ReadOnly Property AutoStart As Boolean Implements ITest.AutoStart
        Get
            Return True
        End Get
    End Property

    Public Property Running As Boolean Implements ITest.Running
        Get
            Return _Running
        End Get
        Set(value As Boolean)
            _Running = value
        End Set
    End Property
    Private _Running As Boolean

    Private Sub ButtonStartStop_Click(sender As Object, e As RoutedEventArgs) Handles ButtonStartStop.Click
        If Running Then
            ITest_StopTest()
        Else
            ITest_StartTest()
        End If
    End Sub

    Private Sub ITest_StartTest() Implements ITest.StartTest
        If Running Then Return
        ' Ensure any previous run is fully finished
        If _runningTask IsNot Nothing AndAlso Not _runningTask.IsCompleted Then
            _cts?.Cancel()
            Try : _runningTask.Wait(3000) : Catch : End Try
        End If
        CleanupServer()
        Running = True
        ButtonStartStop.Content = "Stop Test"
        _cts = New CancellationTokenSource()
        Dispatcher.InvokeAsync(Sub()
                                   TextLog.Text = String.Empty
                                   LabelPassed.Text = "0"
                                   LabelFailed.Text = "0"
                                   LabelTotal.Text = "0"
                               End Sub)
        _runningTask = Task.Run(Async Function() As Task
                                    Await RunAllTests(_cts.Token)
                                End Function)
    End Sub

    Private Sub ITest_StopTest() Implements ITest.StopTest
        If Not Running Then Return
        _cts?.Cancel()
        ' Wait for the running task to finish before cleaning up
        If _runningTask IsNot Nothing AndAlso Not _runningTask.IsCompleted Then
            Try : _runningTask.Wait(5000) : Catch : End Try
        End If
        CleanupServer()
        Running = False
        Dispatcher.InvokeAsync(Sub() ButtonStartStop.Content = "Start Test")
    End Sub

#End Region

#Region "Logging & Byte Tracking"

    ''' <summary>Per-test context for metrics, byte tracking, and cycles. Uses AsyncLocal for parallel safety.</summary>
    Private Class TestContext
        Public BytesSent As Long = 0
        Public BytesReceived As Long = 0
        Public CycleCount As Integer = 0
        Public Sw As Stopwatch
        Public CpuStart As TimeSpan
        Public MemStart As Long
        Public Captured As Boolean = False
        Public WallMs As Long = 0
        Public CpuMs As Double = 0
        Public MemDelta As Long = 0

        Public Sub Capture()
            If Captured Then Return
            Captured = True
            Sw.Stop()
            WallMs = Sw.ElapsedMilliseconds
            Dim proc = Process.GetCurrentProcess()
            proc.Refresh()
            CpuMs = (proc.TotalProcessorTime - CpuStart).TotalMilliseconds
            MemDelta = proc.PrivateMemorySize64 - MemStart
        End Sub
    End Class

    Private _testContext As New Threading.AsyncLocal(Of TestContext)

    Private Sub TrackSent(byteCount As Integer)
        Dim ctx = _testContext.Value
        If ctx IsNot Nothing Then Interlocked.Add(ctx.BytesSent, byteCount)
    End Sub

    Private Sub TrackReceived(byteCount As Integer)
        Dim ctx = _testContext.Value
        If ctx IsNot Nothing Then Interlocked.Add(ctx.BytesReceived, byteCount)
    End Sub

    Private Sub TrackCycle()
        Dim ctx = _testContext.Value
        If ctx IsNot Nothing Then Interlocked.Increment(ctx.CycleCount)
    End Sub

    Private Sub StopMetrics()
        _testContext.Value?.Capture()
    End Sub

    Private Shared Function FormatBytes(bytes As Long) As String
        If bytes < 1024 Then Return bytes & " B"
        If bytes < 1024 * 1024 Then Return (bytes / 1024.0).ToString("N1") & " KB"
        Return (bytes / (1024.0 * 1024)).ToString("N2") & " MB"
    End Function

    Private ReadOnly _logBuffer As New Concurrent.ConcurrentQueue(Of String)

    Public Sub Log(text As String)
        If text Is Nothing OrElse text = String.Empty Then Return
        _logBuffer.Enqueue(text)
    End Sub

    ''' <summary>Flushes all buffered log lines to the TextBox in a single UI dispatch.</summary>
    Private Sub FlushLog()
        If _logBuffer.IsEmpty Then Return
        Dim sb As New StringBuilder()
        Dim line As String = Nothing
        While _logBuffer.TryDequeue(line)
            Dim chars = line.ToCharArray()
            For i = 0 To chars.Length - 1
                If AscW(chars(i)) >= 256 Then chars(i) = "?"c
            Next
            sb.AppendLine(New String(chars))
        End While
        If sb.Length = 0 Then Return
        Dim batch = sb.ToString()
        Try
            Dispatcher.Invoke(Sub()
                                  Try
                                      SyncLock TextLog
                                          Dim isAtEnd As Boolean = TextLog.VerticalOffset >= (TextLog.ExtentHeight - TextLog.ViewportHeight) * 0.9
                                          Try : TextLog.AppendText(batch)
                                          Catch : End Try
                                          Try : If isAtEnd Then TextLog.ScrollToEnd()
                                          Catch : End Try
                                      End SyncLock
                                  Catch : End Try
                              End Sub)
        Catch : End Try
    End Sub

    Private Sub ServerLog(text As String)
        Log("[Server] " & text)
    End Sub

    ''' <summary>Prints a fancy section banner in the log.</summary>
    Private Sub LogSection(title As String)
        Log("")
        Log("[Test] " & New String("=", 60))
        Log("[Test]   " & title)
        Log("[Test] " & New String("=", 60))
        Log("")
    End Sub

    ''' <summary>Prints a lightweight sub-section divider.</summary>
    Private Sub LogDivider()
        Log("[Test] " & New String("-", 48))
    End Sub

#End Region

#Region "Test Runner"

    Private _passed As Integer = 0
    Private _failed As Integer = 0
    Private _suiteStopwatch As Stopwatch
    Private _allResults As New List(Of TestResult)

    ''' <summary>
    ''' Complexity weights for rating calculation. Higher = more complex protocol work.
    ''' Tests not listed default to 1.0.
    ''' </summary>
    Private Shared ReadOnly Complexity As New Dictionary(Of String, Double) From {
        {"Property_Http", 0.2}, {"Property_SocketJack", 0.2}, {"Property_WebSocket", 0.2},
        {"RegisterProtocol_Custom", 1.5}, {"RemoveProtocol", 0.3},
        {"Server_Listen", 0.5},
        {"Http_IndexPage", 1.0}, {"Http_Robots", 1.0}, {"Http_MapGet", 1.2}, {"Http_MapPost", 1.4},
        {"Http_RemoveRoute", 0.8}, {"Http_MapDirectory", 1.5}, {"Http_DirectoryListing", 1.6},
        {"Http_MapStream", 2.0}, {"Http_MapUploadStream", 2.5}, {"Http_HeadRequest", 1.0},
        {"Http_OnHttpRequest", 1.3}, {"Http_ChunkedSettings", 0.3},
        {"SJ_Connect", 1.5}, {"SJ_SendReceive", 2.5}, {"SJ_ClientConnectedEvent", 1.8},
        {"SJ_Broadcast", 3.0}, {"SJ_BroadcastExcept", 3.2},
        {"WS_Handshake_101", 2.0}, {"WS_ClientConnectedEvent", 1.8},
        {"WS_SendReceive", 3.0}, {"WS_PingPong", 2.0}, {"WS_CloseFrame", 1.5},
        {"Broadcast_Register", 1.0}, {"Broadcast_StreamActive", 1.2},
        {"Cross_HttpAndSocketJack", 3.5}, {"Cross_HttpAndWebSocket", 3.0},
        {"Cross_BroadcastSkipsHttp", 3.5},
        {"Dispose_NoThrow", 0.5},
        {"Ser_RoundTrip", 1.0}, {"Ser_LargeObject", 1.5}, {"Ser_TypePreservation", 0.8},
        {"Cmp_Deflate", 1.0}, {"Cmp_GZip", 1.0}, {"Cmp_RoundTrip", 1.5}
    }

    ''' <summary>
    ''' Computes a 1.0-10.0 performance rating from byte throughput and complexity.
    ''' Higher throughput per ms at higher complexity = higher rating.
    ''' </summary>
    Private Shared Function ComputeRating(testName As String, elapsedMs As Long, totalBytes As Long) As Double
        Dim cw As Double = 1.0
        Complexity.TryGetValue(testName, cw)

        ' Throughput: bytes per millisecond (KB/s effectively)
        Dim throughput As Double = If(elapsedMs > 0, totalBytes / CDbl(elapsedMs), totalBytes)

        ' Base score: log-scale throughput * complexity weight, clamped to 1-10
        ' Property-only tests (0 bytes, tiny time) get a flat score from complexity alone
        Dim score As Double
        If totalBytes = 0 Then
            score = Math.Min(cw * 3.0, 10.0)
        Else
            score = (Math.Log10(throughput + 1) * 1.2 + 0.5) * (cw / 2.5)
        End If

        Return Math.Round(Math.Max(1.0, Math.Min(10.0, score)), 1)
    End Function

    Private Async Function RunAllTests(ct As CancellationToken) As Task
        ' Let the UI finish rendering before starting tests to avoid compute bleed from window loading
        Await Task.Delay(1500, ct)

        _passed = 0
        _failed = 0
        _allResults.Clear()
        _suiteStopwatch = Stopwatch.StartNew()

        Dim resetAction As Action =
            Sub()
                For Each r In TestResults
                    r.Status = "..."
                    r.Bytes = ""
                    r.Compute = ""
                    r.Memory = ""
                    r.Rating = ""
                    r.ResultText = ""
                    r.Cycles = ""
                    r.Duration = ""
                    r.CpuPercent = 0
                    r.Error = ""
                Next
            End Sub
        Dispatcher.Invoke(resetAction)

        Log("[Test] " & New String("*", 60))
        Log("[Test] ***   MutableTcpServer Comprehensive Test Suite   ***")
        Log("[Test] ***   All protocols on single port: " & ServerPort.ToString().PadRight(16) & "***")
        Log("[Test] ***   Started: " & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss").PadRight(30) & "***")
        Log("[Test] " & New String("*", 60))
        FlushLog()

        Dim parallel As Boolean = False
        Dispatcher.Invoke(Sub() parallel = CheckRunParallel.IsChecked = True)

        ' ── Property Tests ── (always sequential - setup)
        LogSection("PROPERTY ACCESSOR TESTS")
        Await RunTest("Property_Http", AddressOf Test_Property_Http, ct)
        Await RunTest("Property_SocketJack", AddressOf Test_Property_SocketJack, ct)
        Await RunTest("Property_WebSocket", AddressOf Test_Property_WebSocket, ct)

        ' ── Protocol Registration ── (always sequential - setup)
        LogSection("PROTOCOL REGISTRATION TESTS")
        Await RunTest("RegisterProtocol_Custom", AddressOf Test_RegisterProtocol_Custom, ct)
        Await RunTest("RemoveProtocol", AddressOf Test_RemoveProtocol, ct)

        ' ── Server Lifecycle ── (always sequential - setup)
        LogSection("SERVER LIFECYCLE")
        Await RunTest("Server_Listen", AddressOf Test_Server_Listen, ct)

        If Server Is Nothing OrElse Not Server.IsListening Then
            Log("[Test] Server failed to start. Aborting remaining tests.")
            PrintFinalSummary()
            Return
        End If

        If parallel Then
            Log("[Test] Running remaining tests in PARALLEL mode...")
            Dim tasks As New List(Of Task)

            tasks.Add(Task.Run(Async Function()
                                   LogSection("HTTP PROTOCOL TESTS")
                                   Await RunTest("Http_IndexPage", AddressOf Test_Http_IndexPage, ct)
                                   Await RunTest("Http_Robots", AddressOf Test_Http_Robots, ct)
                                   Await RunTest("Http_MapGet", AddressOf Test_Http_MapGet, ct)
                                   Await RunTest("Http_MapPost", AddressOf Test_Http_MapPost, ct)
                                   Await RunTest("Http_RemoveRoute", AddressOf Test_Http_RemoveRoute, ct)
                                   Await RunTest("Http_MapDirectory", AddressOf Test_Http_MapDirectory, ct)
                                   Await RunTest("Http_DirectoryListing", AddressOf Test_Http_DirectoryListing, ct)
                                   Await RunTest("Http_MapStream", AddressOf Test_Http_MapStream, ct)
                                   Await RunTest("Http_MapUploadStream", AddressOf Test_Http_MapUploadStream, ct)
                                   Await RunTest("Http_HeadRequest", AddressOf Test_Http_HeadRequest, ct)
                                   Await RunTest("Http_OnHttpRequest", AddressOf Test_Http_OnHttpRequest, ct)
                                   Await RunTest("Http_ChunkedSettings", AddressOf Test_Http_ChunkedSettings, ct)
                               End Function))

            tasks.Add(Task.Run(Async Function()
                                   LogSection("SOCKETJACK PROTOCOL TESTS")
                                   Await RunTest("SJ_Connect", AddressOf Test_SJ_Connect, ct)
                                   Await RunTest("SJ_SendReceive", AddressOf Test_SJ_SendReceive, ct)
                                   Await RunTest("SJ_ClientConnectedEvent", AddressOf Test_SJ_ClientConnectedEvent, ct)
                                   Await RunTest("SJ_Broadcast", AddressOf Test_SJ_Broadcast, ct)
                                   Await RunTest("SJ_BroadcastExcept", AddressOf Test_SJ_BroadcastExcept, ct)
                               End Function))

            tasks.Add(Task.Run(Async Function()
                                   LogSection("WEBSOCKET PROTOCOL TESTS")
                                   Await RunTest("WS_Handshake_101", AddressOf Test_WS_Handshake, ct)
                                   Await RunTest("WS_ClientConnectedEvent", AddressOf Test_WS_ClientConnectedEvent, ct)
                                   Await RunTest("WS_SendReceive", AddressOf Test_WS_SendReceive, ct)
                                   Await RunTest("WS_PingPong", AddressOf Test_WS_PingPong, ct)
                                   Await RunTest("WS_CloseFrame", AddressOf Test_WS_CloseFrame, ct)
                               End Function))

            tasks.Add(Task.Run(Async Function()
                                   LogSection("SERIALIZATION TESTS")
                                   Await RunTest("Ser_RoundTrip", AddressOf Test_Ser_RoundTrip, ct)
                                   Await RunTest("Ser_LargeObject", AddressOf Test_Ser_LargeObject, ct)
                                   Await RunTest("Ser_TypePreservation", AddressOf Test_Ser_TypePreservation, ct)
                               End Function))

            tasks.Add(Task.Run(Async Function()
                                   LogSection("COMPRESSION TESTS")
                                   Await RunTest("Cmp_Deflate", AddressOf Test_Cmp_Deflate, ct)
                                   Await RunTest("Cmp_GZip", AddressOf Test_Cmp_GZip, ct)
                                   Await RunTest("Cmp_RoundTrip", AddressOf Test_Cmp_RoundTrip, ct)
                               End Function))

            Await Task.WhenAll(tasks)

            ' Broadcast and Cross-Protocol run after parallel group (they have dependencies)
            LogSection("BROADCAST SERVER TESTS")
            Await RunTest("Broadcast_Register", AddressOf Test_Broadcast_Register, ct)
            Await RunTest("Broadcast_StreamActive", AddressOf Test_Broadcast_StreamActive, ct)

            LogSection("CROSS-PROTOCOL TESTS")
            Await RunTest("Cross_HttpAndSocketJack", AddressOf Test_Cross_HttpAndSocketJack, ct)
            Await RunTest("Cross_HttpAndWebSocket", AddressOf Test_Cross_HttpAndWebSocket, ct)
            Await RunTest("Cross_BroadcastSkipsHttp", AddressOf Test_Cross_BroadcastSkipsHttp, ct)
        Else
            ' ── HTTP Tests ──
            LogSection("HTTP PROTOCOL TESTS")
            Await RunTest("Http_IndexPage", AddressOf Test_Http_IndexPage, ct)
            Await RunTest("Http_Robots", AddressOf Test_Http_Robots, ct)
            Await RunTest("Http_MapGet", AddressOf Test_Http_MapGet, ct)
            Await RunTest("Http_MapPost", AddressOf Test_Http_MapPost, ct)
            Await RunTest("Http_RemoveRoute", AddressOf Test_Http_RemoveRoute, ct)
            Await RunTest("Http_MapDirectory", AddressOf Test_Http_MapDirectory, ct)
            Await RunTest("Http_DirectoryListing", AddressOf Test_Http_DirectoryListing, ct)
            Await RunTest("Http_MapStream", AddressOf Test_Http_MapStream, ct)
            Await RunTest("Http_MapUploadStream", AddressOf Test_Http_MapUploadStream, ct)
            Await RunTest("Http_HeadRequest", AddressOf Test_Http_HeadRequest, ct)
            Await RunTest("Http_OnHttpRequest", AddressOf Test_Http_OnHttpRequest, ct)
            Await RunTest("Http_ChunkedSettings", AddressOf Test_Http_ChunkedSettings, ct)

            ' ── SocketJack Protocol Tests ──
            LogSection("SOCKETJACK PROTOCOL TESTS")
            Await RunTest("SJ_Connect", AddressOf Test_SJ_Connect, ct)
            Await RunTest("SJ_SendReceive", AddressOf Test_SJ_SendReceive, ct)
            Await RunTest("SJ_ClientConnectedEvent", AddressOf Test_SJ_ClientConnectedEvent, ct)
            Await RunTest("SJ_Broadcast", AddressOf Test_SJ_Broadcast, ct)
            Await RunTest("SJ_BroadcastExcept", AddressOf Test_SJ_BroadcastExcept, ct)

            ' ── WebSocket Tests ──
            LogSection("WEBSOCKET PROTOCOL TESTS")
            Await RunTest("WS_Handshake_101", AddressOf Test_WS_Handshake, ct)
            Await RunTest("WS_ClientConnectedEvent", AddressOf Test_WS_ClientConnectedEvent, ct)
            Await RunTest("WS_SendReceive", AddressOf Test_WS_SendReceive, ct)
            Await RunTest("WS_PingPong", AddressOf Test_WS_PingPong, ct)
            Await RunTest("WS_CloseFrame", AddressOf Test_WS_CloseFrame, ct)

            ' ── BroadcastServer Tests ──
            LogSection("BROADCAST SERVER TESTS")
            Await RunTest("Broadcast_Register", AddressOf Test_Broadcast_Register, ct)
            Await RunTest("Broadcast_StreamActive", AddressOf Test_Broadcast_StreamActive, ct)

            ' ── Cross-Protocol Tests ──
            LogSection("CROSS-PROTOCOL TESTS")
            Await RunTest("Cross_HttpAndSocketJack", AddressOf Test_Cross_HttpAndSocketJack, ct)
            Await RunTest("Cross_HttpAndWebSocket", AddressOf Test_Cross_HttpAndWebSocket, ct)
            Await RunTest("Cross_BroadcastSkipsHttp", AddressOf Test_Cross_BroadcastSkipsHttp, ct)

            ' ── Serialization Tests ──
            LogSection("SERIALIZATION TESTS")
            Await RunTest("Ser_RoundTrip", AddressOf Test_Ser_RoundTrip, ct)
            Await RunTest("Ser_LargeObject", AddressOf Test_Ser_LargeObject, ct)
            Await RunTest("Ser_TypePreservation", AddressOf Test_Ser_TypePreservation, ct)

            ' ── Compression Tests ──
            LogSection("COMPRESSION TESTS")
            Await RunTest("Cmp_Deflate", AddressOf Test_Cmp_Deflate, ct)
            Await RunTest("Cmp_GZip", AddressOf Test_Cmp_GZip, ct)
            Await RunTest("Cmp_RoundTrip", AddressOf Test_Cmp_RoundTrip, ct)
        End If

        ' ── Dispose Tests ── (always last)
        LogSection("DISPOSE / CLEANUP TESTS")
        Await RunTest("Dispose_NoThrow", AddressOf Test_Dispose_NoThrow, ct)

        PrintFinalSummary()
    End Function

    Private Async Function RunTest(testName As String, testFunc As Func(Of CancellationToken, Task), ct As CancellationToken) As Task
        If ct.IsCancellationRequested Then Return

        Dim result As TestResult = Nothing
        Dispatcher.Invoke(Sub() result = TestResults.FirstOrDefault(Function(r) r.Name = testName))
        If result Is Nothing OrElse Not result.IsSelected Then Return

        ' Serialize test execution: only one test runs at a time
        Await _testGate.WaitAsync(ct)
        Try
            result.Status = "RUN"
            LogDivider()
            Log("[Test] >> " & testName)

            ' Initialize per-test context (thread-safe via AsyncLocal)
            Dim ctx As New TestContext()
            Dim proc = Process.GetCurrentProcess()
            ctx.CpuStart = proc.TotalProcessorTime
            ctx.MemStart = proc.PrivateMemorySize64
            ctx.Sw = Stopwatch.StartNew()
            _testContext.Value = ctx

            Dim passed As Boolean = False
            Dim errMsg As String = ""
            Try
                Await testFunc(ct)
                passed = True
            Catch ex As OperationCanceledException
                errMsg = "Cancelled"
            Catch ex As Exception
                errMsg = ex.Message
            End Try

            ' If the test didn't explicitly call StopMetrics(), capture now
            ctx.Capture()

            Dim wallMs As Long = ctx.WallMs
            Dim cpuUsedMs As Double = ctx.CpuMs
            Dim memDelta As Long = ctx.MemDelta
            Dim cpuPercent As Double = If(wallMs > 0, (cpuUsedMs / (wallMs * Environment.ProcessorCount)) * 100.0, 0)
            cpuPercent = Math.Max(0, Math.Min(100, cpuPercent))
            Dim computePts As Double = Math.Round(cpuUsedMs * (cpuPercent / 100.0), 2)

            Dim totalBytes = Interlocked.Read(ctx.BytesSent) + Interlocked.Read(ctx.BytesReceived)
            Dim cycles = Math.Max(ctx.CycleCount, 1)
            Dim rating = ComputeRating(testName, wallMs, totalBytes)

            result.ElapsedMs = wallMs
            result.ByteCount = totalBytes
            result.RatingValue = rating
            result.ComputePoints = computePts
            result.CpuTimeMs = cpuUsedMs
            result.MemoryDeltaBytes = memDelta
            result.Bytes = FormatBytes(totalBytes)
            result.Rating = rating.ToString("N1")
            result.Compute = FormatComputePoints(computePts) & " (" & cpuPercent.ToString("N0") & "%)"
            result.Memory = FormatMemory(memDelta)
            result.CycleCount = cycles
            result.Cycles = cycles.ToString()
            result.Duration = FormatElapsed(wallMs)
            result.CpuPercent = cpuPercent

            If passed Then
                Interlocked.Increment(_passed)
                result.Status = "PASS"
                result.ResultText = "PASS"
                Log(String.Format("[Test]    PASS  Cycles {0}  I/O {1}  CPU {2}% ({3})  Mem {4}  Rating {5}",
                                  cycles, FormatBytes(totalBytes),
                                  cpuPercent.ToString("N0"), FormatComputePoints(computePts),
                                  FormatMemory(memDelta), rating.ToString("N1")))
            Else
                Interlocked.Increment(_failed)
                result.Status = "FAIL"
                result.ResultText = "FAIL"
                result.Error = errMsg
                Log(String.Format("[Test]    FAIL  Cycles {0}  I/O {1}  CPU {2}% ({3})  Mem {4}  Rating {5}",
                                  cycles, FormatBytes(totalBytes),
                                  cpuPercent.ToString("N0"), FormatComputePoints(computePts),
                                  FormatMemory(memDelta), rating.ToString("N1")))
                Log("[Test]    Error: " & errMsg)
            End If

            SyncLock _allResults
                _allResults.Add(result)
            End SyncLock
            UpdateSummary()
            FlushLog()
        Finally
            _testGate.Release()
        End Try
    End Function

    Private Sub PrintFinalSummary()
        _suiteStopwatch?.Stop()
        Dim totalMs = If(_suiteStopwatch IsNot Nothing, _suiteStopwatch.ElapsedMilliseconds, 0L)
        Dim totalBytesAll As Long = _allResults.Sum(Function(r) r.ByteCount)
        Dim avgRating As Double = If(_allResults.Count > 0, _allResults.Average(Function(r) r.RatingValue), 0)
        Dim totalCompute As Double = _allResults.Sum(Function(r) r.ComputePoints)
        Dim peakMemDelta As Long = If(_allResults.Count > 0, _allResults.Max(Function(r) r.MemoryDeltaBytes), 0)

        Log("")
        Log("[Test] " & New String("=", 96))
        Log("[Test]   FINAL RESULTS")
        Log("[Test] " & New String("=", 96))
        Log("")

        ' Header
        Log(String.Format("[Test]  {0}  {1}  {2}  {3}  {4}  {5}  {6}",
                          "Status".PadRight(6), "Test".PadRight(28),
                          "Cycles".PadLeft(7), "I/O".PadLeft(10),
                          "Compute".PadLeft(10), "Memory".PadLeft(10),
                          "Rating".PadLeft(6)))
        Log("[Test]  " & New String("-", 84))

        ' Each row
        For Each r In _allResults
            Log(String.Format("[Test]  {0}  {1}  {2}  {3}  {4}  {5}  {6}",
                              r.Status.PadRight(6),
                              r.Name.PadRight(28),
                              r.CycleCount.ToString().PadLeft(7),
                              FormatBytes(r.ByteCount).PadLeft(10),
                              FormatComputePoints(r.ComputePoints).PadLeft(10),
                              FormatMemory(r.MemoryDeltaBytes).PadLeft(10),
                              r.RatingValue.ToString("N1").PadLeft(6)))
        Next

        Log("[Test]  " & New String("-", 84))

        ' Totals
        Log("")
        Log("[Test] " & New String("*", 60))
        Log(String.Format("[Test] ***  Passed: {0}   Failed: {1}   Total: {2}", _passed, _failed, _passed + _failed).PadRight(59) & "*")
        Log(String.Format("[Test] ***  Total Cycles:  {0}", _allResults.Sum(Function(r) r.CycleCount)).PadRight(59) & "*")
        Log(String.Format("[Test] ***  Total I/O:     {0}", FormatBytes(totalBytesAll)).PadRight(59) & "*")
        Log(String.Format("[Test] ***  Total Compute: {0}", FormatComputePoints(totalCompute)).PadRight(59) & "*")
        Log(String.Format("[Test] ***  Peak Mem:      {0}", FormatMemory(peakMemDelta)).PadRight(59) & "*")
        Dim totalCpuMs As Double = _allResults.Sum(Function(r) r.CpuTimeMs)
        Log(String.Format("[Test] ***  Total Time:    {0}", FormatElapsed(totalMs)).PadRight(59) & "*")
        Log(String.Format("[Test] ***  Total CPU:     {0}", FormatElapsed(CLng(totalCpuMs))).PadRight(59) & "*")
        Log(String.Format("[Test] ***  Avg Rating:    {0} / 10.0", avgRating.ToString("N1")).PadRight(59) & "*")
        Log(String.Format("[Test] ***  Throughput:    {0}/s", FormatBytes(If(totalMs > 0, totalBytesAll * 1000L \ totalMs, totalBytesAll))).PadRight(59) & "*")

        Dim verdict As String
        If _failed = 0 Then
            verdict = "ALL TESTS PASSED"
        ElseIf _failed <= 2 Then
            verdict = "MOSTLY PASSED (" & _failed & " failure(s))"
        Else
            verdict = "FAILED (" & _failed & " failure(s))"
        End If
        Log(String.Format("[Test] ***  Verdict:       {0}", verdict).PadRight(59) & "*")
        Log("[Test] " & New String("*", 60))

        UpdateSummary()
        FlushLog()
        Dispatcher.InvokeAsync(Sub()
                                   Running = False
                                   ButtonStartStop.Content = "Start Test"
                               End Sub)
    End Sub

    Private Shared Function FormatElapsed(ms As Long) As String
        If ms < 1000 Then Return ms & "ms"
        If ms < 60000 Then Return (ms / 1000.0).ToString("N2") & "s"
        Return TimeSpan.FromMilliseconds(ms).ToString("m\:ss\.fff")
    End Function

    ''' <summary>Format compute points with appropriate scale suffix.</summary>
    Private Shared Function FormatComputePoints(pts As Double) As String
        If pts < 0.01 Then Return "0 cp"
        If pts < 1 Then Return pts.ToString("N2") & " cp"
        If pts < 1000 Then Return pts.ToString("N1") & " cp"
        If pts < 1000000 Then Return (pts / 1000.0).ToString("N1") & " kcp"
        Return (pts / 1000000.0).ToString("N2") & " Mcp"
    End Function

    ''' <summary>Format a memory delta (may be negative if GC ran).</summary>
    Private Shared Function FormatMemory(bytes As Long) As String
        Dim prefix As String = If(bytes < 0, "-", "+")
        Dim abs As Long = Math.Abs(bytes)
        If abs < 1024 Then Return prefix & abs & " B"
        If abs < 1024 * 1024 Then Return prefix & (abs / 1024.0).ToString("N1") & " KB"
        Return prefix & (abs / (1024.0 * 1024)).ToString("N2") & " MB"
    End Function

    Private Sub UpdateSummary()
        Dispatcher.InvokeAsync(Sub()
                                   LabelPassed.Text = _passed.ToString()
                                   LabelFailed.Text = _failed.ToString()
                                   LabelTotal.Text = (_passed + _failed).ToString()
                               End Sub)
    End Sub

#End Region

#Region "Server Setup/Cleanup"

    Private Sub CreateServer()
        Dim opts As New NetworkOptions With {
            .UsePeerToPeer = False,
            .UseCompression = False,
            .Logging = True
        }
        Server = New MutableTcpServer(opts, ServerPort, "TestMutableServer")
        Server.EnabledProtocols.UnionWith({MutableTcpProtocols.Http, MutableTcpProtocols.SocketJack, MutableTcpProtocols.WebSocket, MutableTcpProtocols.Rtmp})
        AddHandler Server.LogOutput, AddressOf ServerLog

        Server.RegisterCallback(Of MutableTestMessage)(Sub(e)
                                                       End Sub)

        Server.Http.IndexPageHtml = "<html><body><h1>MutableTcpServer Test</h1></body></html>"
        Server.Http.Robots = "User-agent: *" & vbLf & "Disallow: /secret" & vbLf
    End Sub

    Private Sub CleanupServer()
        Try
            Broadcast = Nothing
        Catch : End Try
        Try
            If Server IsNot Nothing Then
                If Server.IsListening Then Server.StopListening()
                Server.Dispose()
                Server = Nothing
            End If
        Catch : End Try
        Try
            If _tempDir IsNot Nothing AndAlso Directory.Exists(_tempDir) Then
                Directory.Delete(_tempDir, True)
                _tempDir = Nothing
            End If
        Catch : End Try
        Try
            _cts?.Dispose()
            _cts = Nothing
        Catch : End Try
    End Sub

#End Region

#Region "Property Tests"

    Private Async Function Test_Property_Http(ct As CancellationToken) As Task
        Using s = New MutableTcpServer(ServerPort + 100, "PropTest")
            Assert(s.Http IsNot Nothing, "Http property should not be Nothing")
        End Using
    End Function

    Private Async Function Test_Property_SocketJack(ct As CancellationToken) As Task
        Using s = New MutableTcpServer(ServerPort + 101, "PropTest")
            Assert(s.SocketJack IsNot Nothing, "SocketJack property should not be Nothing")
        End Using
    End Function

    Private Async Function Test_Property_WebSocket(ct As CancellationToken) As Task
        Using s = New MutableTcpServer(ServerPort + 102, "PropTest")
            Assert(s.WebSocket IsNot Nothing, "WebSocket property should not be Nothing")
        End Using
    End Function

#End Region

#Region "Protocol Registration Tests"

    Private Async Function Test_RegisterProtocol_Custom(ct As CancellationToken) As Task
        Using s = New MutableTcpServer(ServerPort + 103, "RegTest")
            Dim handled As New TaskCompletionSource(Of Boolean)()
            Dim custom As New DummyProtocolHandler("MAGIC", True, Sub() handled.TrySetResult(True))
            s.RegisterProtocol(custom)
            s.EnabledProtocols.Add(custom.Name)

            Assert(s.Listen(), "Server should start listening")

            Using tcp As New System.Net.Sockets.TcpClient()
                tcp.Connect("127.0.0.1", ServerPort + 103)
                Dim stream = tcp.GetStream()
                Dim data = Encoding.UTF8.GetBytes("MAGIC-PAYLOAD-DATA-HERE" & vbCrLf & vbCrLf)
                Await stream.WriteAsync(data, 0, data.Length)
                Await stream.FlushAsync()
                TrackSent(data.Length)

                Dim ok = Await Task.WhenAny(handled.Task, Task.Delay(TimeoutMs)) Is handled.Task
                Assert(ok, "Custom protocol handler should process the data")
                tcp.Close()
            End Using

            s.StopListening()
        End Using
    End Function

    Private Async Function Test_RemoveProtocol(ct As CancellationToken) As Task
        Using s = New MutableTcpServer(ServerPort + 104, "RemTest")
            Dim custom As New DummyProtocolHandler("Custom", False, Nothing)
            s.RegisterProtocol(custom)
            Assert(s.RemoveProtocol(custom), "RemoveProtocol should return True for registered handler")
            Assert(Not s.RemoveProtocol(custom), "RemoveProtocol should return False for unregistered handler")
        End Using
    End Function

#End Region

#Region "Server Lifecycle"

    Private Async Function Test_Server_Listen(ct As CancellationToken) As Task
        CreateServer()
        Assert(Server.Listen(), "Server should start listening")
        Assert(Server.IsListening, "Server should report IsListening = True")
    End Function

#End Region

#Region "HTTP Tests"

    Private Async Function Test_Http_IndexPage(ct As CancellationToken) As Task
        Using client As New Net.Http.HttpClient()
            Dim response = Await client.GetAsync("http://127.0.0.1:" & ServerPort & "/")
            Dim body = Await response.Content.ReadAsStringAsync()
            TrackReceived(Encoding.UTF8.GetByteCount(body))
            StopMetrics()
            Assert(response.StatusCode = Net.HttpStatusCode.OK, "Expected 200 OK, got " & response.StatusCode.ToString())
            Assert(body.Contains("MutableTcpServer Test"), "Body should contain index page content")
            Assert(body.Contains("<html>"), "Body should be valid HTML")
            Assert(body.Contains("</html>"), "Body should contain closing HTML tag")
        End Using
    End Function

    Private Async Function Test_Http_Robots(ct As CancellationToken) As Task
        Using client As New Net.Http.HttpClient()
            Dim response = Await client.GetAsync("http://127.0.0.1:" & ServerPort & "/robots.txt")
            Dim body = Await response.Content.ReadAsStringAsync()
            TrackReceived(Encoding.UTF8.GetByteCount(body))
            StopMetrics()
            Assert(response.StatusCode = Net.HttpStatusCode.OK, "Expected 200 OK")
            Assert(body.Contains("User-agent"), "robots.txt should contain User-agent directive")
            Assert(body.Contains("Disallow"), "robots.txt should contain Disallow directive")
            Assert(body.Contains("/secret"), "robots.txt should disallow /secret")
        End Using
    End Function

    Private Async Function Test_Http_MapGet(ct As CancellationToken) As Task
        Server.Http.Map("GET", "/api/hello", Function(conn, req, token) "Hello from MutableTcpServer!")
        Using client As New Net.Http.HttpClient()
            Dim response = Await client.GetAsync("http://127.0.0.1:" & ServerPort & "/api/hello")
            Dim body = Await response.Content.ReadAsStringAsync()
            TrackReceived(Encoding.UTF8.GetByteCount(body))
            StopMetrics()
            Assert(response.StatusCode = Net.HttpStatusCode.OK, "Expected 200 OK")
            Assert(body = "Hello from MutableTcpServer!", "GET route should return exact mapped content")
        End Using
    End Function

    Private Async Function Test_Http_MapPost(ct As CancellationToken) As Task
        Server.Http.Map("POST", "/api/echo", Function(conn, req, token) If(req.Body, "empty"))
        Dim sent = "echo-payload"
        Using client As New Net.Http.HttpClient()
            Dim content As New Net.Http.StringContent(sent, Encoding.UTF8, "text/plain")
            TrackSent(Encoding.UTF8.GetByteCount(sent))
            Dim response = Await client.PostAsync("http://127.0.0.1:" & ServerPort & "/api/echo", content)
            Dim body = Await response.Content.ReadAsStringAsync()
            TrackReceived(Encoding.UTF8.GetByteCount(body))
            StopMetrics()
            Assert(response.StatusCode = Net.HttpStatusCode.OK, "Expected 200 OK")
            Assert(body.Contains(sent), "POST route should echo back the sent payload")
            Assert(body.Length >= sent.Length, "Response should be at least as long as sent payload")
        End Using
    End Function

    Private Async Function Test_Http_RemoveRoute(ct As CancellationToken) As Task
        Server.Http.Map("GET", "/api/temp", Function(conn, req, token) "temp-content")
        Dim removed = Server.Http.RemoveRoute("GET", "/api/temp")
        Assert(removed, "RemoveRoute should return True for existing route")
        Using client As New Net.Http.HttpClient()
            Dim response = Await client.GetAsync("http://127.0.0.1:" & ServerPort & "/api/temp")
            Dim body = Await response.Content.ReadAsStringAsync()
            Assert(body <> "temp-content", "Removed route should no longer serve its handler")
        End Using
    End Function

    Private Async Function Test_Http_MapDirectory(ct As CancellationToken) As Task
        _tempDir = Path.Combine(Path.GetTempPath(), "SocketJackMutableTest_" & ServerPort)
        Directory.CreateDirectory(_tempDir)
        Dim expected = "static-file-content"
        File.WriteAllText(Path.Combine(_tempDir, "test.txt"), expected)
        Server.Http.MapDirectory("/static", _tempDir)
        Using client As New Net.Http.HttpClient()
            Dim response = Await client.GetAsync("http://127.0.0.1:" & ServerPort & "/static/test.txt")
            Dim body = Await response.Content.ReadAsStringAsync()
            TrackReceived(Encoding.UTF8.GetByteCount(body))
            StopMetrics()
            Assert(response.StatusCode = Net.HttpStatusCode.OK, "Expected 200 OK for static file")
            Assert(body = expected, "Static file content should match exactly")
        End Using
    End Function

    Private Async Function Test_Http_DirectoryListing(ct As CancellationToken) As Task
        Server.Http.AllowDirectoryListing = True
        Dim listDir = Path.Combine(Path.GetTempPath(), "SocketJackMutableList_" & ServerPort)
        Directory.CreateDirectory(listDir)
        Try
            File.WriteAllText(Path.Combine(listDir, "file1.txt"), "a")
            File.WriteAllText(Path.Combine(listDir, "file2.txt"), "b")
            Server.Http.MapDirectory("/browse", listDir)
            Using client As New Net.Http.HttpClient()
                Dim response = Await client.GetAsync("http://127.0.0.1:" & ServerPort & "/browse")
                Dim body = Await response.Content.ReadAsStringAsync()
                TrackReceived(Encoding.UTF8.GetByteCount(body))
                StopMetrics()
                Assert(response.StatusCode = Net.HttpStatusCode.OK, "Expected 200 OK for listing")
                Assert(body.Contains("file1.txt"), "Listing should contain file1.txt")
                Assert(body.Contains("file2.txt"), "Listing should contain file2.txt")
            End Using
        Finally
            Server.Http.RemoveDirectoryMapping("/browse")
            Try : Directory.Delete(listDir, True) : Catch : End Try
        End Try
    End Function

    Private Async Function Test_Http_MapStream(ct As CancellationToken) As Task
        Server.Http.MapStream("GET", "/stream/test",
            Async Function(conn, req, chunked, token) As Task
                Await Task.Run(Sub()
                                   chunked.WriteLine("line1")
                                   chunked.WriteLine("line2")
                               End Sub)
            End Function)
        Using client As New Net.Http.HttpClient()
            Dim response = Await client.GetAsync("http://127.0.0.1:" & ServerPort & "/stream/test")
            Dim body = Await response.Content.ReadAsStringAsync()
            TrackReceived(Encoding.UTF8.GetByteCount(body))
            StopMetrics()
            Assert(response.StatusCode = Net.HttpStatusCode.OK, "Expected 200 OK for stream")
            Assert(body.Contains("line1"), "Stream should contain line1")
            Assert(body.Contains("line2"), "Stream should contain line2")
        End Using
    End Function

    Private Async Function Test_Http_MapUploadStream(ct As CancellationToken) As Task
        Dim uploadData As New TaskCompletionSource(Of String)(TaskCreationOptions.RunContinuationsAsynchronously)

        Server.Http.MapUploadStream("POST", "/upload/test",
            Sub(conn, req, upload, token)
                Dim sb As New StringBuilder()
                If req.BodyBytes IsNot Nothing AndAlso req.BodyBytes.Length > 0 Then
                    sb.Append(Encoding.UTF8.GetString(req.BodyBytes))
                End If
                uploadData.TrySetResult(sb.ToString())
            End Sub)

        Dim payload = "upload-payload"
        TrackSent(Encoding.UTF8.GetByteCount(payload))
        Using client As New Net.Http.HttpClient()
            client.Timeout = TimeSpan.FromMilliseconds(TimeoutMs)
            Dim content As New Net.Http.StringContent(payload, Encoding.UTF8, "application/octet-stream")
            Try
                Await client.PostAsync("http://127.0.0.1:" & ServerPort & "/upload/test", content)
            Catch
            End Try
        End Using

        Dim ok = Await Task.WhenAny(uploadData.Task, Task.Delay(TimeoutMs)) Is uploadData.Task
        If ok Then TrackReceived(Encoding.UTF8.GetByteCount(uploadData.Task.Result))
        StopMetrics()

        Assert(ok, "Upload handler should receive body data")
        Assert(uploadData.Task.Result.Contains(payload), "Upload body should contain exact payload")
        Assert(uploadData.Task.Result.Length >= payload.Length, "Received data should be at least payload length")
    End Function

    Private Async Function Test_Http_HeadRequest(ct As CancellationToken) As Task
        Server.Http.Map("GET", "/headtest", Function(conn, req, token) "body-content")
        Using client As New Net.Http.HttpClient()
            Dim request As New Net.Http.HttpRequestMessage(Net.Http.HttpMethod.Head, "http://127.0.0.1:" & ServerPort & "/headtest")
            Dim response = Await client.SendAsync(request)
            Dim body = Await response.Content.ReadAsStringAsync()
            TrackReceived(Encoding.UTF8.GetByteCount(body))
            StopMetrics()
            Assert(response.StatusCode = Net.HttpStatusCode.OK, "Expected 200 OK for HEAD")
            Assert(body.Length = 0 OrElse Not body.Contains("body-content"), "HEAD response body should be empty")
        End Using
    End Function

    Private Async Function Test_Http_OnHttpRequest(ct As CancellationToken) As Task
        Dim eventFired As New TaskCompletionSource(Of String)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim handler As HttpServer.RequestHandler =
            Sub(conn, ByRef context, token)
                eventFired.TrySetResult(context.Request.Path)
                context.Response.Body = "custom-handler"
            End Sub
        AddHandler Server.Http.OnHttpRequest, handler
        Try
            Using client As New Net.Http.HttpClient()
                Dim response = Await client.GetAsync("http://127.0.0.1:" & ServerPort & "/unmapped-route-test")
                Dim body = Await response.Content.ReadAsStringAsync()
                TrackReceived(Encoding.UTF8.GetByteCount(body))
                Dim ok = Await Task.WhenAny(eventFired.Task, Task.Delay(TimeoutMs)) Is eventFired.Task
                StopMetrics()
                Assert(ok, "OnHttpRequest event should fire for unmapped routes")
                Assert(eventFired.Task.Result = "/unmapped-route-test", "Event should receive the requested path")
                Assert(body.Contains("custom-handler"), "Response body should contain custom handler output")
            End Using
        Finally
            RemoveHandler Server.Http.OnHttpRequest, handler
        End Try
    End Function

    Private Async Function Test_Http_ChunkedSettings(ct As CancellationToken) As Task
        Server.Http.ChunkedThreshold = 2048
        Assert(Server.Http.ChunkedThreshold = 2048, "ChunkedThreshold should be 2048")
        Server.Http.ChunkedSize = 4096
        Assert(Server.Http.ChunkedSize = 4096, "ChunkedSize should be 4096")
    End Function

#End Region

#Region "SocketJack Protocol Tests"

    Private Async Function Test_SJ_Connect(ct As CancellationToken) As Task
        Using client As New SocketJack.Net.TcpClient()
            client.Options.UsePeerToPeer = False
            client.Options.UseCompression = False
            client.Options.Whitelist.Add(GetType(MutableTestMessage))
            Await client.Connect("127.0.0.1", ServerPort)
            Assert(client.Connected, "SocketJack client should connect")
            client.Disconnect()
        End Using
    End Function

    Private Async Function Test_SJ_SendReceive(ct As CancellationToken) As Task
        Dim receivedTcs As New TaskCompletionSource(Of MutableTestMessage)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim cbHandler As Action(Of ReceivedEventArgs(Of MutableTestMessage)) =
            Sub(e) receivedTcs.TrySetResult(e.Object)
        Server.RegisterCallback(Of MutableTestMessage)(cbHandler)

        Try
            Using client As New SocketJack.Net.TcpClient()
                client.Options.UsePeerToPeer = False
                client.Options.UseCompression = False
                client.Options.Whitelist.Add(GetType(MutableTestMessage))
                Await client.Connect("127.0.0.1", ServerPort)

                Dim outMsg As New MutableTestMessage With {.Text = "RoundTrip", .Number = 42}
                Dim serialized = Server.Options.Serializer.Serialize(New Wrapper(outMsg, Server))
                TrackSent(serialized.Length)
                client.Send(outMsg)

                Dim result = Await Task.WhenAny(receivedTcs.Task, Task.Delay(TimeoutMs))
                If result Is receivedTcs.Task Then
                    Dim inMsg = receivedTcs.Task.Result
                    Dim inSerialized = Server.Options.Serializer.Serialize(New Wrapper(inMsg, Server))
                    TrackReceived(inSerialized.Length)
                End If
                StopMetrics()

                Assert(client.Connected, "Client should connect")
                Assert(result Is receivedTcs.Task, "Should receive message within timeout")
                Dim msg = receivedTcs.Task.Result
                Assert(msg.Text = "RoundTrip", "Text should be 'RoundTrip', got '" & msg.Text & "'")
                Assert(msg.Number = 42, "Number should be 42, got " & msg.Number)
                client.Disconnect()
            End Using
        Finally
            Server.RegisterCallback(Of MutableTestMessage)(Sub(e)
                                                           End Sub)
        End Try
    End Function

    Private Async Function Test_SJ_ClientConnectedEvent(ct As CancellationToken) As Task
        Dim connectedTcs As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim handler As Action(Of NetworkConnection) = Sub(conn) connectedTcs.TrySetResult(True)
        AddHandler Server.SocketJackClientConnected, handler

        Try
            Using client As New SocketJack.Net.TcpClient()
                client.Options.UsePeerToPeer = False
                client.Options.UseCompression = False
                client.Options.Whitelist.Add(GetType(MutableTestMessage))
                Await client.Connect("127.0.0.1", ServerPort)
                client.Send(New MutableTestMessage With {.Text = "detect", .Number = 0})

                Dim ok = Await Task.WhenAny(connectedTcs.Task, Task.Delay(TimeoutMs)) Is connectedTcs.Task
                Assert(ok, "SocketJackClientConnected event should fire")
                client.Disconnect()
            End Using
        Finally
            RemoveHandler Server.SocketJackClientConnected, handler
        End Try
    End Function

    Private Async Function Test_SJ_Broadcast(ct As CancellationToken) As Task
        Dim received As New Concurrent.ConcurrentBag(Of String)()
        Dim allReceived As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)

        ' Use SocketJackClientConnected event to detect when both clients are recognized
        Dim sjCount As Integer = 0
        Dim bothReady As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim sjHandler As Action(Of NetworkConnection) = Sub(conn)
                                                            If Interlocked.Increment(sjCount) >= 2 Then bothReady.TrySetResult(True)
                                                        End Sub
        AddHandler Server.SocketJackClientConnected, sjHandler

        Dim clients As New List(Of SocketJack.Net.TcpClient)()
        Try
            For i = 0 To 1
                Dim c As New SocketJack.Net.TcpClient()
                c.Options.UsePeerToPeer = False
                c.Options.UseCompression = False
                c.Options.Whitelist.Add(GetType(MutableTestMessage))
                c.RegisterCallback(Of MutableTestMessage)(Sub(e)
                                                              received.Add(e.Object.Text & ":" & e.Object.Number)
                                                              If received.Count >= 2 Then allReceived.TrySetResult(True)
                                                          End Sub)
                Await c.Connect("127.0.0.1", ServerPort)
                c.Send(New MutableTestMessage With {.Text = "init" & i, .Number = i})
                clients.Add(c)
            Next

            ' Wait for server to recognize both clients as SocketJack protocol
            Dim ready = Await Task.WhenAny(bothReady.Task, Task.Delay(TimeoutMs)) Is bothReady.Task
            Assert(ready, "Both clients should be recognized as SocketJack protocol")
            Dim bcastMsg As New MutableTestMessage With {.Text = "broadcast", .Number = 99}
            Dim bcastBytes = Server.Options.Serializer.Serialize(New Wrapper(bcastMsg, Server))
            TrackSent(bcastBytes.Length * 2) ' sent to 2 clients
            Server.SendBroadcast(bcastMsg)

            Dim ok = Await Task.WhenAny(allReceived.Task, Task.Delay(TimeoutMs)) Is allReceived.Task
            If ok Then TrackReceived(bcastBytes.Length * 2)
            StopMetrics()

            Assert(ok, "Both SocketJack clients should receive the broadcast")
            Assert(received.Count >= 2, "Should have received at least 2 messages, got " & received.Count)
            Assert(received.All(Function(r) r.StartsWith("broadcast:")), "All received should have text 'broadcast'")
            Assert(received.All(Function(r) r.EndsWith(":99")), "All received should have Number=99")
        Finally
            RemoveHandler Server.SocketJackClientConnected, sjHandler
            For Each c In clients
                c.Disconnect()
                c.Dispose()
            Next
        End Try
    End Function

    Private Async Function Test_SJ_BroadcastExcept(ct As CancellationToken) As Task
        Dim received As New Concurrent.ConcurrentBag(Of String)()
        Dim excReceived As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)

        ' Collect actual SocketJack connections so Except targets a real SJ client
        Dim sjConnections As New Concurrent.ConcurrentBag(Of NetworkConnection)()
        Dim bothReady As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim sjHandler As Action(Of NetworkConnection) = Sub(conn)
                                                            sjConnections.Add(conn)
                                                            If sjConnections.Count >= 2 Then bothReady.TrySetResult(True)
                                                        End Sub
        AddHandler Server.SocketJackClientConnected, sjHandler

        Dim client1 As New SocketJack.Net.TcpClient()
        client1.Options.UsePeerToPeer = False
        client1.Options.UseCompression = False
        Dim client2 As New SocketJack.Net.TcpClient()
        client2.Options.UsePeerToPeer = False
        client2.Options.UseCompression = False
        Try
            client1.Options.Whitelist.Add(GetType(MutableTestMessage))
            client1.RegisterCallback(Of MutableTestMessage)(Sub(e)
                                                                received.Add("c1:" & e.Object.Text & ":" & e.Object.Number)
                                                                If e.Object.Text = "exc" Then excReceived.TrySetResult(True)
                                                            End Sub)
            Await client1.Connect("127.0.0.1", ServerPort)
            client1.Send(New MutableTestMessage With {.Text = "init1", .Number = 1})

            client2.Options.Whitelist.Add(GetType(MutableTestMessage))
            client2.RegisterCallback(Of MutableTestMessage)(Sub(e)
                                                                received.Add("c2:" & e.Object.Text & ":" & e.Object.Number)
                                                                If e.Object.Text = "exc" Then excReceived.TrySetResult(True)
                                                            End Sub)
            Await client2.Connect("127.0.0.1", ServerPort)
            client2.Send(New MutableTestMessage With {.Text = "init2", .Number = 2})

            ' Wait for server to recognize both clients as SocketJack protocol
            Dim ready = Await Task.WhenAny(bothReady.Task, Task.Delay(TimeoutMs)) Is bothReady.Task
            Assert(ready, "Both clients should be recognized as SocketJack protocol")

            Dim allConns = sjConnections.ToArray()
            Dim excMsg As New MutableTestMessage With {.Text = "exc", .Number = 77}
            Dim excBytes = Server.Options.Serializer.Serialize(New Wrapper(excMsg, Server))
            TrackSent(excBytes.Length)
            Server.SendBroadcast(excMsg, allConns(0))

            Dim ok = Await Task.WhenAny(excReceived.Task, Task.Delay(TimeoutMs)) Is excReceived.Task
            If ok Then TrackReceived(excBytes.Length)
            StopMetrics()

            Assert(allConns.Length >= 2, "Should have at least 2 connections")
            Assert(ok, "At least one client should receive the broadcast")
            Dim excItems = received.Where(Function(r) r.Contains("exc")).ToArray()
            Assert(excItems.All(Function(r) r.Contains(":77")), "Received exc messages should have Number=77")
        Finally
            RemoveHandler Server.SocketJackClientConnected, sjHandler
            client1.Disconnect() : client1.Dispose()
            client2.Disconnect() : client2.Dispose()
        End Try
    End Function

#End Region

#Region "WebSocket Tests"

    Private Async Function Test_WS_Handshake(ct As CancellationToken) As Task
        Using tcp As New System.Net.Sockets.TcpClient()
            tcp.Connect("127.0.0.1", ServerPort)
            Dim stream = tcp.GetStream()

            Dim key = Convert.ToBase64String(Encoding.UTF8.GetBytes("ws-handshake-test"))
            Dim request = "GET /ws HTTP/1.1" & vbCrLf &
                          "Host: 127.0.0.1:" & ServerPort & vbCrLf &
                          "Upgrade: websocket" & vbCrLf &
                          "Connection: Upgrade" & vbCrLf &
                          "Sec-WebSocket-Key: " & key & vbCrLf &
                          "Sec-WebSocket-Version: 13" & vbCrLf & vbCrLf

            Dim reqBytes = Encoding.UTF8.GetBytes(request)
            Await stream.WriteAsync(reqBytes, 0, reqBytes.Length)
            Await stream.FlushAsync()
            TrackSent(reqBytes.Length)

            Dim buf(4095) As Byte
            Dim read = Await ReadWithTimeout(stream, buf, TimeoutMs)
            TrackReceived(read)
            StopMetrics()

            Assert(read > 0, "Should receive a response")
            Dim response = Encoding.UTF8.GetString(buf, 0, read)
            Assert(response.Contains("101 Switching Protocols"), "Should respond with 101 Switching Protocols")
            Assert(response.Contains("Sec-WebSocket-Accept"), "Should include Sec-WebSocket-Accept header")

            ' Verify accept key per RFC 6455
            Dim expectedAccept As String
            Using sha1 As SHA1 = SHA1.Create()
                Dim hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(key & "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))
                expectedAccept = Convert.ToBase64String(hash)
            End Using
            Assert(response.Contains(expectedAccept), "Sec-WebSocket-Accept should match RFC 6455 computation")

            tcp.Close()
        End Using
    End Function

    Private Async Function Test_WS_ClientConnectedEvent(ct As CancellationToken) As Task
        Dim connectedTcs As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim handler As Action(Of NetworkConnection) = Sub(conn) connectedTcs.TrySetResult(True)
        AddHandler Server.WebSocketClientConnected, handler

        Try
            Using tcp As New System.Net.Sockets.TcpClient()
                tcp.Connect("127.0.0.1", ServerPort)
                Dim stream = tcp.GetStream()
                Dim key = Convert.ToBase64String(Encoding.UTF8.GetBytes("ws-event-test!!!"))
                Dim request = "GET / HTTP/1.1" & vbCrLf &
                              "Host: 127.0.0.1:" & ServerPort & vbCrLf &
                              "Upgrade: websocket" & vbCrLf &
                              "Connection: Upgrade" & vbCrLf &
                              "Sec-WebSocket-Key: " & key & vbCrLf &
                              "Sec-WebSocket-Version: 13" & vbCrLf & vbCrLf
                Await stream.WriteAsync(Encoding.UTF8.GetBytes(request), 0, Encoding.UTF8.GetByteCount(request))

                Dim ok = Await Task.WhenAny(connectedTcs.Task, Task.Delay(TimeoutMs)) Is connectedTcs.Task
                Assert(ok, "WebSocketClientConnected event should fire after upgrade")
                tcp.Close()
            End Using
        Finally
            RemoveHandler Server.WebSocketClientConnected, handler
        End Try
    End Function

    Private Async Function Test_WS_SendReceive(ct As CancellationToken) As Task
        Dim receivedTcs As New TaskCompletionSource(Of MutableTestMessage)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim cbHandler As Action(Of ReceivedEventArgs(Of MutableTestMessage)) =
            Sub(e) receivedTcs.TrySetResult(e.Object)
        Server.RegisterCallback(Of MutableTestMessage)(cbHandler)

        Try
            Using tcp As New System.Net.Sockets.TcpClient()
                tcp.Connect("127.0.0.1", ServerPort)
                Dim stream = tcp.GetStream()

                ' Perform WebSocket handshake
                Dim key = Convert.ToBase64String(Encoding.UTF8.GetBytes("ws-text-frame!!!"))
                Dim request = "GET / HTTP/1.1" & vbCrLf &
                              "Host: 127.0.0.1:" & ServerPort & vbCrLf &
                              "Upgrade: websocket" & vbCrLf &
                              "Connection: Upgrade" & vbCrLf &
                              "Sec-WebSocket-Key: " & key & vbCrLf &
                              "Sec-WebSocket-Version: 13" & vbCrLf & vbCrLf
                Await stream.WriteAsync(Encoding.UTF8.GetBytes(request), 0, Encoding.UTF8.GetByteCount(request))
                Await stream.FlushAsync()

                ' Read 101 response
                Dim buf(4095) As Byte
                Await ReadWithTimeout(stream, buf, TimeoutMs)

                ' Send a WebSocket text frame with serialized TestMessage
                Dim msg As New MutableTestMessage With {.Text = "WsTest", .Number = 7}
                Dim payload = Server.Options.Serializer.Serialize(New Wrapper(msg, Server))
                Dim frame = BuildClientFrame(payload, isText:=True)
                Await stream.WriteAsync(frame, 0, frame.Length)
                Await stream.FlushAsync()
                TrackSent(frame.Length)

                Dim result = Await Task.WhenAny(receivedTcs.Task, Task.Delay(TimeoutMs))
                StopMetrics()

                Assert(result Is receivedTcs.Task, "Server should receive the WebSocket text frame")
                Dim received = receivedTcs.Task.Result
                Assert(received.Text = "WsTest", "Text should be 'WsTest', got '" & received.Text & "'")
                Assert(received.Number = 7, "Number should be 7, got " & received.Number)
                tcp.Close()
            End Using
        Finally
            Server.RegisterCallback(Of MutableTestMessage)(Sub(e)
                                                           End Sub)
        End Try
    End Function

    Private Async Function Test_WS_PingPong(ct As CancellationToken) As Task
        Using tcp As New System.Net.Sockets.TcpClient()
            tcp.Connect("127.0.0.1", ServerPort)
            Dim stream = tcp.GetStream()

            ' Handshake
            Dim key = Convert.ToBase64String(Encoding.UTF8.GetBytes("ws-ping-test!!!!"))
            Dim request = "GET / HTTP/1.1" & vbCrLf &
                          "Host: 127.0.0.1:" & ServerPort & vbCrLf &
                          "Upgrade: websocket" & vbCrLf &
                          "Connection: Upgrade" & vbCrLf &
                          "Sec-WebSocket-Key: " & key & vbCrLf &
                          "Sec-WebSocket-Version: 13" & vbCrLf & vbCrLf
            Await stream.WriteAsync(Encoding.UTF8.GetBytes(request), 0, Encoding.UTF8.GetByteCount(request))
            Await stream.FlushAsync()

            Dim buf(4095) As Byte
            Await ReadWithTimeout(stream, buf, TimeoutMs)

            ' Send Ping frame (opcode 0x9)
            Dim pingPayload = Encoding.UTF8.GetBytes("ping")
            Dim pingFrame = BuildClientFrame(pingPayload, opcode:=&H9)
            Await stream.WriteAsync(pingFrame, 0, pingFrame.Length)
            Await stream.FlushAsync()
            TrackSent(pingFrame.Length)

            ' Read Pong response
            Dim read = Await ReadWithTimeout(stream, buf, TimeoutMs)
            TrackReceived(read)
            StopMetrics()

            Assert(read >= 2, "Should receive a Pong response")
            Dim pongOpcode = buf(0) And &HF
            Assert(pongOpcode = &HA, "Response should be a Pong frame (opcode 0xA), got 0x" & pongOpcode.ToString("X"))
            ' Verify pong echoes back the ping payload
            If read > 2 Then
                Dim pongLen = CInt(buf(1) And &H7F)
                If pongLen = 4 Then
                    Dim pongBody = Encoding.UTF8.GetString(buf, 2, 4)
                    Assert(pongBody = "ping", "Pong payload should echo 'ping', got '" & pongBody & "'")
                End If
            End If
            tcp.Close()
        End Using
    End Function

    Private Async Function Test_WS_CloseFrame(ct As CancellationToken) As Task
        Using tcp As New System.Net.Sockets.TcpClient()
            tcp.Connect("127.0.0.1", ServerPort)
            Dim stream = tcp.GetStream()

            ' Handshake
            Dim key = Convert.ToBase64String(Encoding.UTF8.GetBytes("ws-close-test!!x"))
            Dim request = "GET / HTTP/1.1" & vbCrLf &
                          "Host: 127.0.0.1:" & ServerPort & vbCrLf &
                          "Upgrade: websocket" & vbCrLf &
                          "Connection: Upgrade" & vbCrLf &
                          "Sec-WebSocket-Key: " & key & vbCrLf &
                          "Sec-WebSocket-Version: 13" & vbCrLf & vbCrLf
            Await stream.WriteAsync(Encoding.UTF8.GetBytes(request), 0, Encoding.UTF8.GetByteCount(request))
            Await stream.FlushAsync()

            Dim buf(4095) As Byte
            Await ReadWithTimeout(stream, buf, TimeoutMs)

            ' Send Close frame (opcode 0x8)
            Dim closeFrame = BuildClientFrame(Array.Empty(Of Byte)(), opcode:=&H8)
            Await stream.WriteAsync(closeFrame, 0, closeFrame.Length)
            Await stream.FlushAsync()
            TrackSent(closeFrame.Length)

            ' Read response
            Dim read = 0
            Try
                read = Await ReadWithTimeout(stream, buf, 3000)
                TrackReceived(read)
            Catch : End Try
            StopMetrics()

            ' Either close frame back or connection terminated - both valid
            If read >= 2 Then
                Dim closeOpcode = buf(0) And &HF
                Assert(closeOpcode = &H8, "Response should be a Close frame (opcode 0x8)")
            End If
            ' If read = 0, connection was closed, which is also acceptable
            tcp.Close()
        End Using
    End Function

#End Region

#Region "BroadcastServer Tests"

    Private Async Function Test_Broadcast_Register(ct As CancellationToken) As Task
        Broadcast = New BroadcastServer(Server)
        AddHandler Broadcast.LogOutput, AddressOf ServerLog
        Broadcast.Register()
        Assert(Broadcast IsNot Nothing, "BroadcastServer should be created")
        Assert(Broadcast.StreamKey IsNot Nothing AndAlso Broadcast.StreamKey.Length > 0, "StreamKey should be generated")
    End Function

    Private Async Function Test_Broadcast_StreamActive(ct As CancellationToken) As Task
        Using client As New Net.Http.HttpClient()
            Dim response = Await client.GetAsync("http://127.0.0.1:" & ServerPort & "/stream/active")
            Dim body = Await response.Content.ReadAsStringAsync()
            TrackReceived(Encoding.UTF8.GetByteCount(body))
            StopMetrics()
            Assert(response.StatusCode = Net.HttpStatusCode.OK, "Expected 200 OK for /stream/active")
            Assert(body.Contains("active"), "Response should contain 'active' field")
            Assert(body.Contains("viewers"), "Response should contain 'viewers' field")
        End Using
    End Function

#End Region

#Region "Cross-Protocol Tests"

    Private Async Function Test_Cross_HttpAndSocketJack(ct As CancellationToken) As Task
        ' HTTP request
        Dim httpBody As String
        Using httpClient As New Net.Http.HttpClient()
            Dim httpResponse = Await httpClient.GetAsync("http://127.0.0.1:" & ServerPort & "/")
            httpBody = Await httpResponse.Content.ReadAsStringAsync()
            TrackReceived(Encoding.UTF8.GetByteCount(httpBody))
        End Using

        ' SocketJack connection on same port
        Dim sjReceived As New TaskCompletionSource(Of MutableTestMessage)(TaskCreationOptions.RunContinuationsAsynchronously)
        Server.RegisterCallback(Of MutableTestMessage)(Sub(e) sjReceived.TrySetResult(e.Object))

        Try
            Using sjClient As New SocketJack.Net.TcpClient()
                sjClient.Options.UsePeerToPeer = False
                sjClient.Options.UseCompression = False
                sjClient.Options.Whitelist.Add(GetType(MutableTestMessage))
                Await sjClient.Connect("127.0.0.1", ServerPort)

                Dim outMsg As New MutableTestMessage With {.Text = "cross", .Number = 99}
                Dim outBytes = Server.Options.Serializer.Serialize(New Wrapper(outMsg, Server))
                TrackSent(outBytes.Length)
                sjClient.Send(outMsg)

                Dim result = Await Task.WhenAny(sjReceived.Task, Task.Delay(TimeoutMs))
                If result Is sjReceived.Task Then
                    Dim inBytes = Server.Options.Serializer.Serialize(New Wrapper(sjReceived.Task.Result, Server))
                    TrackReceived(inBytes.Length)
                End If
                StopMetrics()

                Assert(httpBody.Contains("MutableTcpServer Test"), "HTTP body should contain index content")
                Assert(sjClient.Connected, "SocketJack client should connect on same port")
                Assert(result Is sjReceived.Task, "SocketJack message should arrive on same port as HTTP")
                Assert(sjReceived.Task.Result.Text = "cross", "Text should be 'cross', got '" & sjReceived.Task.Result.Text & "'")
                Assert(sjReceived.Task.Result.Number = 99, "Number should be 99, got " & sjReceived.Task.Result.Number)
                sjClient.Disconnect()
            End Using
        Finally
            Server.RegisterCallback(Of MutableTestMessage)(Sub(e)
                                                           End Sub)
        End Try
    End Function

    Private Async Function Test_Cross_HttpAndWebSocket(ct As CancellationToken) As Task
        ' HTTP request
        Dim httpBody As String
        Using httpClient As New Net.Http.HttpClient()
            Dim response = Await httpClient.GetAsync("http://127.0.0.1:" & ServerPort & "/api/hello")
            httpBody = Await response.Content.ReadAsStringAsync()
            TrackReceived(Encoding.UTF8.GetByteCount(httpBody))
        End Using

        ' WebSocket handshake on same port
        Dim wsResponse As String
        Using tcp As New System.Net.Sockets.TcpClient()
            tcp.Connect("127.0.0.1", ServerPort)
            Dim stream = tcp.GetStream()
            Dim key = Convert.ToBase64String(Encoding.UTF8.GetBytes("cross-ws-test!!!"))
            Dim request = "GET / HTTP/1.1" & vbCrLf &
                          "Host: 127.0.0.1:" & ServerPort & vbCrLf &
                          "Upgrade: websocket" & vbCrLf &
                          "Connection: Upgrade" & vbCrLf &
                          "Sec-WebSocket-Key: " & key & vbCrLf &
                          "Sec-WebSocket-Version: 13" & vbCrLf & vbCrLf
            Await stream.WriteAsync(Encoding.UTF8.GetBytes(request), 0, Encoding.UTF8.GetByteCount(request))
            TrackSent(Encoding.UTF8.GetByteCount(request))

            Dim buf(4095) As Byte
            Dim read = Await ReadWithTimeout(stream, buf, TimeoutMs)
            TrackReceived(read)
            wsResponse = Encoding.UTF8.GetString(buf, 0, read)
            tcp.Close()
        End Using
        StopMetrics()

        Assert(httpBody = "Hello from MutableTcpServer!", "HTTP should return exact mapped content")
        Assert(wsResponse.Contains("101 Switching Protocols"), "WebSocket should connect on same port as HTTP")
        Assert(wsResponse.Contains("Sec-WebSocket-Accept"), "WebSocket response should contain accept header")
    End Function

    Private Async Function Test_Cross_BroadcastSkipsHttp(ct As CancellationToken) As Task
        Dim sjReceived As New TaskCompletionSource(Of MutableTestMessage)(TaskCreationOptions.RunContinuationsAsynchronously)

        ' Use SocketJackClientConnected event to know when the server has identified the protocol
        Dim sjReady As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim sjHandler As Action(Of NetworkConnection) = Sub(conn) sjReady.TrySetResult(True)
        AddHandler Server.SocketJackClientConnected, sjHandler

        ' Connect a SocketJack client
        Using sjClient As New SocketJack.Net.TcpClient()
            sjClient.Options.UsePeerToPeer = False
            sjClient.Options.UseCompression = False
            sjClient.Options.Whitelist.Add(GetType(MutableTestMessage))
            sjClient.RegisterCallback(Of MutableTestMessage)(Sub(e)
                                                                 If e.Object.Text = "bcast" Then sjReceived.TrySetResult(e.Object)
                                                             End Sub)
            Await sjClient.Connect("127.0.0.1", ServerPort)
            sjClient.Send(New MutableTestMessage With {.Text = "detect", .Number = 0})

            ' Wait for server to recognize client as SocketJack protocol (event-driven, not a fixed delay)
            Dim ready = Await Task.WhenAny(sjReady.Task, Task.Delay(TimeoutMs)) Is sjReady.Task
            Assert(ready, "SocketJack client should be recognized by server")

            ' Make an HTTP request (proves broadcast doesn't crash with HTTP connections)
            Using httpClient As New Net.Http.HttpClient()
                Dim httpResponse = Await httpClient.GetAsync("http://127.0.0.1:" & ServerPort & "/")
                Dim body = Await httpResponse.Content.ReadAsStringAsync()
                TrackReceived(Encoding.UTF8.GetByteCount(body))
            End Using

            ' Broadcast should only reach SocketJack clients
            Dim bcastMsg As New MutableTestMessage With {.Text = "bcast", .Number = 42}
            Dim bcastBytes = Server.Options.Serializer.Serialize(New Wrapper(bcastMsg, Server))
            TrackSent(bcastBytes.Length)
            Server.SendBroadcast(bcastMsg)

            Dim ok = Await Task.WhenAny(sjReceived.Task, Task.Delay(TimeoutMs)) Is sjReceived.Task
            If ok Then TrackReceived(bcastBytes.Length)
            StopMetrics()

            Assert(ok, "SocketJack client should receive broadcast (HTTP clients should be skipped)")
            Dim recv = sjReceived.Task.Result
            Assert(recv.Text = "bcast", "Received text should be 'bcast', got '" & recv.Text & "'")
            Assert(recv.Number = 42, "Received number should be 42, got " & recv.Number)
            sjClient.Disconnect()
        End Using
        RemoveHandler Server.SocketJackClientConnected, sjHandler
    End Function

#End Region

#Region "Dispose Tests"

    Private Async Function Test_Dispose_NoThrow(ct As CancellationToken) As Task
        ' Server is cleaned up here - last test
        Try
            If Server IsNot Nothing Then
                Server.StopListening()
                Server.Dispose()
                Server = Nothing
            End If
        Catch ex As Exception
            Throw New Exception("Dispose threw an exception: " & ex.Message)
        End Try

        ' Verify double dispose is safe
        Try
            Using s = New MutableTcpServer(ServerPort + 200, "DisposeTest")
                s.Listen()
                s.Dispose()
            End Using
        Catch ex As Exception
            Throw New Exception("Double dispose threw: " & ex.Message)
        End Try
    End Function

#End Region

#Region "Serialization Tests"

    Private Async Function Test_Ser_RoundTrip(ct As CancellationToken) As Task
        Assert(Server IsNot Nothing, "Server must be running for serialization tests")
        Dim serializer = Server.Options.Serializer
        Dim original As New MutableTestMessage With {.Text = "SerializeTest", .Number = 123}
        Dim wrapped As New Wrapper(original, Server)
        Dim bytes = serializer.Serialize(wrapped)
        TrackSent(bytes.Length)
        TrackCycle()

        Dim deserialized = serializer.Deserialize(bytes)
        TrackReceived(bytes.Length)
        StopMetrics()

        Assert(deserialized IsNot Nothing, "Deserialized wrapper should not be Nothing")
        Assert(deserialized.value IsNot Nothing, "Deserialized value should not be Nothing")
        Dim msg = DirectCast(serializer.GetValue(deserialized.value, GetType(MutableTestMessage), True), MutableTestMessage)
        Assert(msg.Text = "SerializeTest", "Text should be 'SerializeTest', got '" & msg.Text & "'")
        Assert(msg.Number = 123, "Number should be 123, got " & msg.Number)
    End Function

    Private Async Function Test_Ser_LargeObject(ct As CancellationToken) As Task
        Assert(Server IsNot Nothing, "Server must be running for serialization tests")
        Dim serializer = Server.Options.Serializer
        Dim largeText = New String("A"c, 100000)
        Dim original As New MutableTestMessage With {.Text = largeText, .Number = 999}
        Dim wrapped As New Wrapper(original, Server)
        Dim bytes = serializer.Serialize(wrapped)
        TrackSent(bytes.Length)
        TrackCycle()

        Dim deserialized = serializer.Deserialize(bytes)
        TrackReceived(bytes.Length)
        StopMetrics()

        Assert(deserialized IsNot Nothing, "Deserialized wrapper should not be Nothing")
        Dim msg = DirectCast(serializer.GetValue(deserialized.value, GetType(MutableTestMessage), True), MutableTestMessage)
        Assert(msg.Text.Length = 100000, "Text length should be 100000, got " & msg.Text.Length)
        Assert(msg.Number = 999, "Number should be 999, got " & msg.Number)
    End Function

    Private Async Function Test_Ser_TypePreservation(ct As CancellationToken) As Task
        Assert(Server IsNot Nothing, "Server must be running for serialization tests")
        Dim serializer = Server.Options.Serializer
        Dim original As New MutableTestMessage With {.Text = "TypeTest", .Number = 42}
        Dim wrapped As New Wrapper(original, Server)
        Dim bytes = serializer.Serialize(wrapped)
        TrackSent(bytes.Length)
        TrackCycle()

        Dim deserialized = serializer.Deserialize(bytes)
        TrackReceived(bytes.Length)
        StopMetrics()

        Assert(deserialized IsNot Nothing, "Deserialized wrapper should not be Nothing")
        Assert(deserialized.Type IsNot Nothing, "Deserialized Type should not be Nothing")
        Dim resolvedType = deserialized.GetValueType()
        Assert(resolvedType IsNot Nothing, "Resolved type should not be Nothing")
        Assert(resolvedType Is GetType(MutableTestMessage) OrElse resolvedType.Name = "MutableTestMessage",
               "Type should resolve to MutableTestMessage, got " & If(resolvedType?.Name, "Nothing"))
    End Function

#End Region

#Region "Compression Tests"

    Private Async Function Test_Cmp_Deflate(ct As CancellationToken) As Task
        Dim compressor As New DeflateCompression()
        Dim original = Encoding.UTF8.GetBytes(New String("D"c, 50000))
        TrackSent(original.Length)
        TrackCycle()

        Dim compressed = compressor.Compress(original)
        Dim decompressed = compressor.Decompress(compressed)
        TrackReceived(decompressed.Length)
        StopMetrics()

        Assert(compressed.Length < original.Length, "Compressed should be smaller (" & compressed.Length & " vs " & original.Length & ")")
        Assert(decompressed.Length = original.Length, "Decompressed length should match original")
        Assert(original.SequenceEqual(decompressed), "Decompressed data should match original exactly")
    End Function

    Private Async Function Test_Cmp_GZip(ct As CancellationToken) As Task
        Dim compressor As New GZip2Compression()
        Dim original = Encoding.UTF8.GetBytes(New String("G"c, 50000))
        TrackSent(original.Length)
        TrackCycle()

        Dim compressed = compressor.Compress(original)
        Dim decompressed = compressor.Decompress(compressed)
        TrackReceived(decompressed.Length)
        StopMetrics()

        Assert(compressed.Length < original.Length, "Compressed should be smaller (" & compressed.Length & " vs " & original.Length & ")")
        Assert(decompressed.Length = original.Length, "Decompressed length should match original")
        Assert(original.SequenceEqual(decompressed), "Decompressed data should match original exactly")
    End Function

    Private Async Function Test_Cmp_RoundTrip(ct As CancellationToken) As Task
        Dim deflate As New DeflateCompression()
        Dim gzip As New GZip2Compression()
        Dim original = Encoding.UTF8.GetBytes("RoundTrip compression test with mixed data: " & New String("X"c, 5000) & " END")
        TrackSent(original.Length)

        ' Deflate round-trip
        Dim deflateCompressed = deflate.Compress(original)
        Dim deflateDecompressed = deflate.Decompress(deflateCompressed)
        TrackCycle()

        ' GZip round-trip
        Dim gzipCompressed = gzip.Compress(original)
        Dim gzipDecompressed = gzip.Decompress(gzipCompressed)
        TrackCycle()

        TrackReceived(deflateDecompressed.Length + gzipDecompressed.Length)
        StopMetrics()

        Assert(deflateDecompressed.Length = original.Length, "Deflate decompressed length should match original")
        Assert(gzipDecompressed.Length = original.Length, "GZip decompressed length should match original")
        Assert(original.SequenceEqual(deflateDecompressed), "Deflate decompressed should match original")
        Assert(original.SequenceEqual(gzipDecompressed), "GZip decompressed should match original")
        Assert(deflateCompressed.Length < original.Length, "Deflate compressed should be smaller")
        Assert(gzipCompressed.Length < original.Length, "GZip compressed should be smaller")
    End Function

#End Region

#Region "Helpers"

    Private Sub Assert(condition As Boolean, message As String)
        If condition Then
            Log("[Test]      ✓ " & message)
        Else
            Log("[Test]      ✗ " & message)
            Throw New Exception("Assertion failed: " & message)
        End If
    End Sub

    Private Shared Async Function ReadWithTimeout(stream As NetworkStream, buffer As Byte(), timeoutMs As Integer) As Task(Of Integer)
        Using cts As New CancellationTokenSource(timeoutMs)
            Try
                Return Await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token)
            Catch ex As OperationCanceledException
                Return 0
            End Try
        End Using
    End Function

    ''' <summary>
    ''' Builds a masked WebSocket frame (as a client would send).
    ''' </summary>
    Private Shared Function BuildClientFrame(payload As Byte(), Optional isText As Boolean = False, Optional opcode As Byte = 0) As Byte()
        If opcode = 0 Then
            opcode = If(isText, CByte(&H1), CByte(&H2))
        End If

        Dim fin As Byte = CByte(&H80 Or opcode)
        Dim maskKey As Byte() = {&H12, &H34, &H56, &H78}

        Dim header As Byte()
        If payload.Length <= 125 Then
            header = New Byte(5) {} ' 2 header + 4 mask
            header(0) = fin
            header(1) = CByte(&H80 Or payload.Length)
            Buffer.BlockCopy(maskKey, 0, header, 2, 4)
        ElseIf payload.Length <= 65535 Then
            header = New Byte(7) {} ' 2 header + 2 ext len + 4 mask
            header(0) = fin
            header(1) = &H80 Or 126
            header(2) = CByte((payload.Length >> 8) And &HFF)
            header(3) = CByte(payload.Length And &HFF)
            Buffer.BlockCopy(maskKey, 0, header, 4, 4)
        Else
            header = New Byte(13) {} ' 2 header + 8 ext len + 4 mask
            header(0) = fin
            header(1) = &H80 Or 127
            Dim len As ULong = CULng(payload.Length)
            For i = 0 To 7
                header(2 + i) = CByte((len >> (56 - i * 8)) And &HFF)
            Next
            Buffer.BlockCopy(maskKey, 0, header, 10, 4)
        End If

        ' Mask the payload
        Dim masked(payload.Length - 1) As Byte
        For i = 0 To payload.Length - 1
            masked(i) = payload(i) Xor maskKey(i Mod 4)
        Next

        Dim frame(header.Length + masked.Length - 1) As Byte
        Buffer.BlockCopy(header, 0, frame, 0, header.Length)
        Buffer.BlockCopy(masked, 0, frame, header.Length, masked.Length)
        Return frame
    End Function

    ''' <summary>
    ''' Dummy protocol handler for testing custom handler registration.
    ''' </summary>
    Private Class DummyProtocolHandler
        Implements IProtocolHandler

        Private ReadOnly _match As Boolean
        Private ReadOnly _onProcess As Action

        Public Sub New(name As String, match As Boolean, onProcess As Action)
            _Name = name
            _match = match
            _onProcess = onProcess
        End Sub

        Public ReadOnly Property Name As String Implements IProtocolHandler.Name
            Get
                Return _Name
            End Get
        End Property
        Private ReadOnly _Name As String

        Public Function CanHandle(data As Byte()) As Boolean Implements IProtocolHandler.CanHandle
            Return _match
        End Function

        Public Sub ProcessReceive(server As MutableTcpServer, connection As NetworkConnection, ByRef e As IReceivedEventArgs) Implements IProtocolHandler.ProcessReceive
            _onProcess?.Invoke()
        End Sub

        Public Sub OnDisconnected(server As MutableTcpServer, connection As NetworkConnection) Implements IProtocolHandler.OnDisconnected
        End Sub
    End Class

#End Region

End Class
