Imports System.Collections.ObjectModel
Imports System.ComponentModel
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Windows.Threading
Imports Microsoft.Win32
Imports MonoTorrent
Imports MonoTorrent.Client
Imports SocketJack.Net
Imports SocketJack.Net.Torrent

''' <summary>
''' Full-featured torrent client control built on SocketJack.
''' 
''' Features:
'''  - Menu bar with File (Add Magnet, Seed File, Stop), Edit, View, Options
'''  - Transfer list showing active uploads/downloads with progress, speed, ETA
'''  - RuTracker search tab that scrapes search results for magnet links
'''  - Syntax-highlighted log panel
''' </summary>
Public Class TorrentClientTest
    Implements ITest

#Region "Constants"

    Private Const DefaultPieceSize As Integer = 64 * 1024

#End Region

#Region "Properties"

    Public Property TestName As String = "TorrentClient" Implements ITest.TestName

    Public Property AutoStart As Boolean Implements ITest.AutoStart
        Get
            Return _AutoStart
        End Get
        Set(value As Boolean)
            _AutoStart = value
        End Set
    End Property
    Private _AutoStart As Boolean = False

    Public Property Running As Boolean Implements ITest.Running
        Get
            Return _Running
        End Get
        Set(value As Boolean)
            _Running = value
        End Set
    End Property
    Private _Running As Boolean = False

    Private _PeersConnected As Integer = 0

#End Region

#Region "Fields"

    Private _tracker As TorrentTracker
    Private _seeder As TorrentClient
    Private _mutableServer As MutableTcpServer
    Private _metadata As TorrentMetadata
    Private _downloadDir As String
    Private _trackerPort As Integer
    Private _seederPort As Integer
    Private _cts As CancellationTokenSource
    Private _peerIds As New List(Of String)()
    Private _transfers As New ObservableCollection(Of TransferItem)()
    Private _ruTrackerResults As New ObservableCollection(Of RuTrackerResult)()
    Private _cookieContainer As New System.Net.CookieContainer()
    Private _httpClient As System.Net.Http.HttpClient
    Private _ruTrackerLoggedIn As Boolean = False
    Private _ruTrackerCurrentPage As Integer = 0
    Private _ruTrackerLastQuery As String = ""
    Private Const RuTrackerResultsPerPage As Integer = 50
    Private _speedTimer As System.Windows.Threading.DispatcherTimer
    Private _lastBytesDownloaded As Long = 0
    Private _lastSpeedCheck As DateTime = DateTime.Now
    Private _ruTrackerSearchCts As CancellationTokenSource
    Private _standardBtEngine As ClientEngine

#End Region

#Region "Transfer Item Class"

    Public Class TransferItem
        Implements INotifyPropertyChanged

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Private Sub OnPropertyChanged(name As String)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
        End Sub

        Private _Name As String = ""
        Public Property Name As String
            Get
                Return _Name
            End Get
            Set(value As String)
                _Name = value
                OnPropertyChanged(NameOf(Name))
            End Set
        End Property

        Private _Size As String = "--"
        Public Property Size As String
            Get
                Return _Size
            End Get
            Set(value As String)
                _Size = value
                OnPropertyChanged(NameOf(Size))
            End Set
        End Property

        Private _ProgressValue As Double = 0
        Public Property ProgressValue As Double
            Get
                Return _ProgressValue
            End Get
            Set(value As Double)
                _ProgressValue = value
                OnPropertyChanged(NameOf(ProgressValue))
                OnPropertyChanged(NameOf(ProgressText))
            End Set
        End Property

        Public ReadOnly Property ProgressText As String
            Get
                Return String.Format("{0:F1}%", _ProgressValue)
            End Get
        End Property

        Private _Speed As String = "--"
        Public Property Speed As String
            Get
                Return _Speed
            End Get
            Set(value As String)
                _Speed = value
                OnPropertyChanged(NameOf(Speed))
            End Set
        End Property

        Private _ETA As String = "--"
        Public Property ETA As String
            Get
                Return _ETA
            End Get
            Set(value As String)
                _ETA = value
                OnPropertyChanged(NameOf(ETA))
            End Set
        End Property

        Private _Peers As String = "0"
        Public Property Peers As String
            Get
                Return _Peers
            End Get
            Set(value As String)
                _Peers = value
                OnPropertyChanged(NameOf(Peers))
            End Set
        End Property

        Private _Status As String = "Idle"
        Public Property Status As String
            Get
                Return _Status
            End Get
            Set(value As String)
                _Status = value
                OnPropertyChanged(NameOf(Status))
            End Set
        End Property

        Public Property InfoHash As String
        Public Property MagnetUri As String
        Public Property IsSeeding As Boolean
        Public Property Client As TorrentClient
        Public Property Metadata As TorrentMetadata
        Public Property LastBytes As Long
        Public Property StandardManager As TorrentManager
    End Class

#End Region

#Region "RuTracker Result Class"

    Public Class RuTrackerResult
        Public Property Title As String = ""
        Public Property Size As String = "--"
        Public Property Seeds As String = "0"
        Public Property Leechers As String = "0"
        Public Property MagnetLink As String = ""
        Public Property TopicId As String = ""

        Public ReadOnly Property MagnetShort As String
            Get
                If String.IsNullOrEmpty(MagnetLink) Then Return "--"
                If MagnetLink.Length > 50 Then Return MagnetLink.Substring(0, 50) & "..."
                Return MagnetLink
            End Get
        End Property
    End Class

#End Region

#Region "Initialization"

    Public Sub New()
        InitializeComponent()
        TransferListView.ItemsSource = _transfers
        RuTrackerResultsView.ItemsSource = _ruTrackerResults

        ' Register additional encodings (e.g. windows-1251) for .NET Core+
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)

        ' Create HttpClient with cookie support for RuTracker login
        Dim handler As New System.Net.Http.HttpClientHandler()
        handler.CookieContainer = _cookieContainer
        handler.UseCookies = True
        handler.AllowAutoRedirect = True
        _httpClient = New System.Net.Http.HttpClient(handler)

        ' Wire pagination buttons
        Dim prevBtn = CType(FindName("ButtonRuTrackerPrev"), Button)
        Dim nextBtn = CType(FindName("ButtonRuTrackerNext"), Button)
        If prevBtn IsNot Nothing Then AddHandler prevBtn.Click, AddressOf ButtonRuTrackerPrev_Click
        If nextBtn IsNot Nothing Then AddHandler nextBtn.Click, AddressOf ButtonRuTrackerNext_Click

        _speedTimer = New DispatcherTimer()
        _speedTimer.Interval = TimeSpan.FromSeconds(1)
        AddHandler _speedTimer.Tick, AddressOf SpeedTimerTick
        _speedTimer.Start()
    End Sub

#End Region

#Region "Menu Event Handlers"

    Private Sub MenuAddMagnet_Click(sender As Object, e As RoutedEventArgs) Handles MenuAddMagnet.Click
        Dim dialog As New MagnetLinkDialog()
        dialog.Owner = Window.GetWindow(Me)
        If dialog.ShowDialog() = True Then
            Dim magnetUri As String = dialog.MagnetUri
            If Not String.IsNullOrWhiteSpace(magnetUri) Then
                AddMagnetLink(magnetUri)
            End If
        End If
    End Sub

    Private Sub MenuSeedFile_Click(sender As Object, e As RoutedEventArgs) Handles MenuSeedFile.Click
        Dim dlg As New OpenFileDialog()
        dlg.Title = "Select File to Seed"
        dlg.Filter = "All Files (*.*)|*.*"
        If dlg.ShowDialog() = True Then
            _cts = New CancellationTokenSource()
            SeedFileAsync(dlg.FileName)
        End If
    End Sub

    Private Sub MenuStopAll_Click(sender As Object, e As RoutedEventArgs) Handles MenuStopAll.Click
        StopAll()
    End Sub

    Private Sub MenuClearLog_Click(sender As Object, e As RoutedEventArgs) Handles MenuClearLog.Click
        TextboxLog.Clear()
    End Sub

    Private Sub MenuClearTransfers_Click(sender As Object, e As RoutedEventArgs) Handles MenuClearTransfers.Click
        Dim completed As New List(Of TransferItem)()
        For Each t In _transfers
            If t.Status = "Complete" OrElse t.Status = "Verified" OrElse t.Status = "Stopped" Then
                completed.Add(t)
            End If
        Next
        For Each t In completed
            _transfers.Remove(t)
        Next
        UpdateStatusBar()
    End Sub

    Private Sub MenuToggleLog_Click(sender As Object, e As RoutedEventArgs) Handles MenuToggleLog.Click
        If MenuToggleLog.IsChecked Then
            LogRowDef.Height = New GridLength(200)
            LogSplitter.Visibility = Visibility.Visible
        Else
            LogRowDef.Height = New GridLength(0)
            LogSplitter.Visibility = Visibility.Collapsed
        End If
    End Sub

    Private Sub ButtonRuTrackerSearch_Click(sender As Object, e As RoutedEventArgs)
        Dim query As String = RuTrackerSearchQuery.Text
        If String.IsNullOrWhiteSpace(query) Then Return
        _ruTrackerCurrentPage = 0
        _ruTrackerLastQuery = query
        Task.Run(Sub() SearchRuTracker(query, 0))
    End Sub

    Private Sub ButtonRuTrackerPrev_Click(sender As Object, e As RoutedEventArgs)
        If _ruTrackerCurrentPage <= 0 OrElse String.IsNullOrWhiteSpace(_ruTrackerLastQuery) Then Return
        _ruTrackerCurrentPage -= 1
        Dim page = _ruTrackerCurrentPage
        Dim query = _ruTrackerLastQuery
        Task.Run(Sub() SearchRuTracker(query, page))
    End Sub

    Private Sub ButtonRuTrackerNext_Click(sender As Object, e As RoutedEventArgs)
        If String.IsNullOrWhiteSpace(_ruTrackerLastQuery) Then Return
        _ruTrackerCurrentPage += 1
        Dim page = _ruTrackerCurrentPage
        Dim query = _ruTrackerLastQuery
        Task.Run(Sub() SearchRuTracker(query, page))
    End Sub

#End Region

#Region "RuTracker Result Download"

    Private Sub RuTrackerResult_DoubleClick(sender As Object, e As MouseButtonEventArgs)
        Dim result = TryCast(RuTrackerResultsView.SelectedItem, RuTrackerResult)
        If result Is Nothing Then Return

        If String.IsNullOrWhiteSpace(result.MagnetLink) Then
            Log(String.Format("[RuTracker] No magnet link available for '{0}'", result.Title))
            Return
        End If

        AddMagnetLink(result.MagnetLink, result.Title)
        MainTabs.SelectedItem = TransfersTab
    End Sub

#End Region

#Region "Transfer Context Menu"

    Private Sub CtxCopyMagnet_Click(sender As Object, e As RoutedEventArgs)
        Dim item = TryCast(TransferListView.SelectedItem, TransferItem)
        If item Is Nothing Then Return
        If Not String.IsNullOrEmpty(item.MagnetUri) Then
            Clipboard.SetText(item.MagnetUri)
            Log(String.Format("[UI] Copied magnet link for '{0}'", item.Name))
        Else
            Log(String.Format("[UI] No magnet link available for '{0}'", item.Name))
        End If
    End Sub

    Private Sub CtxCopyHash_Click(sender As Object, e As RoutedEventArgs)
        Dim item = TryCast(TransferListView.SelectedItem, TransferItem)
        If item Is Nothing Then Return
        If Not String.IsNullOrEmpty(item.InfoHash) Then
            Clipboard.SetText(item.InfoHash)
            Log(String.Format("[UI] Copied info hash for '{0}'", item.Name))
        Else
            Log(String.Format("[UI] No info hash available for '{0}'", item.Name))
        End If
    End Sub

    Private Sub CtxStopTransfer_Click(sender As Object, e As RoutedEventArgs)
        Dim item = TryCast(TransferListView.SelectedItem, TransferItem)
        If item Is Nothing Then Return
        If item.Client IsNot Nothing Then
            item.Client.Dispose()
            item.Client = Nothing
        End If
        item.Status = "Stopped"
        item.Speed = "--"
        item.ETA = "--"
        Log(String.Format("[UI] Stopped transfer: '{0}'", item.Name))
    End Sub

    Private Sub CtxRemoveTransfer_Click(sender As Object, e As RoutedEventArgs)
        Dim item = TryCast(TransferListView.SelectedItem, TransferItem)
        If item Is Nothing Then Return
        If item.Client IsNot Nothing Then
            item.Client.Dispose()
            item.Client = Nothing
        End If
        _transfers.Remove(item)
        UpdateStatusBar()
        Log(String.Format("[UI] Removed transfer: '{0}'", item.Name))
    End Sub

#End Region

#Region "Magnet Link Support"

    Private Sub AddMagnetLink(magnetUri As String, Optional preferredName As String = "")
        Log(String.Format("[Magnet] Adding magnet link: {0}", If(magnetUri.Length > 80, magnetUri.Substring(0, 80) & "...", magnetUri)))

        Dim infoHash As String = ExtractInfoHash(magnetUri)
        Dim displayName As String = If(String.IsNullOrWhiteSpace(preferredName), ExtractDisplayName(magnetUri), preferredName)
        If String.IsNullOrEmpty(displayName) Then displayName = "Unknown"

        Dim transfer As New TransferItem()
        transfer.Name = displayName
        transfer.Size = "--"
        transfer.Status = "Magnet (awaiting metadata)"
        transfer.InfoHash = infoHash
        transfer.MagnetUri = magnetUri
        transfer.IsSeeding = False

        Dispatcher.InvokeAsync(Sub()
                                   _transfers.Add(transfer)
                                   UpdateStatusBar()
                               End Sub)

        Dim metadata As TorrentMetadata = Nothing
        If TryCreateSocketJackMetadataFromMagnet(magnetUri, metadata) Then
            transfer.Metadata = metadata
            transfer.InfoHash = metadata.InfoHash
            transfer.Name = If(String.IsNullOrWhiteSpace(displayName), metadata.Name, displayName)
            transfer.Size = FormatBytes(metadata.TotalSize)
            transfer.Status = "Starting"
            transfer.MagnetUri = BuildSocketJackMagnet(metadata)
            StartDownloadFromMetadataAsync(transfer, metadata)
        Else
            transfer.Status = "Starting standard magnet download"
            Log("[Magnet] Standard metadata exchange started (DHT/trackers).")
            StartStandardMagnetDownloadAsync(transfer, magnetUri)
        End If

        Log(String.Format("[Magnet] Added: {0} (hash: {1})", displayName, If(String.IsNullOrEmpty(infoHash), "unknown", infoHash)))
    End Sub

    Private Async Sub StartStandardMagnetDownloadAsync(transfer As TransferItem, magnetUri As String)
        Try
            Dim tempDir = Path.Combine(Path.GetTempPath(), "SocketJack_Torrent")
            If Not Directory.Exists(tempDir) Then Directory.CreateDirectory(tempDir)
            Dim btDir = Path.Combine(tempDir, "bt_downloads")
            If Not Directory.Exists(btDir) Then Directory.CreateDirectory(btDir)

            If _standardBtEngine Is Nothing Then
                Dim settingsBuilder As New EngineSettingsBuilder()
                _standardBtEngine = New ClientEngine(settingsBuilder.ToSettings())
            End If

            Dim magnet = MagnetLink.Parse(magnetUri)
            Dim manager = Await _standardBtEngine.AddAsync(magnet, btDir)
            transfer.StandardManager = manager
            transfer.Status = "Fetching metadata"
            Log("[Magnet] Waiting for metadata from peers...")

            Await manager.StartAsync()
            Await manager.WaitForMetadataAsync()

            If manager.Torrent IsNot Nothing Then
                transfer.Name = manager.Torrent.Name
                transfer.Size = FormatBytes(manager.Torrent.Size)
                transfer.Status = "Downloading"
                Log(String.Format("[Magnet] Metadata loaded: {0} ({1})", transfer.Name, transfer.Size))
            Else
                transfer.Status = "Downloading"
            End If
        Catch ex As Exception
            transfer.Status = "Failed"
            Log("[Magnet] Standard magnet start failed: " & ex.Message)
        End Try
    End Sub

    Private Shared Function ExtractInfoHash(magnetUri As String) As String
        Dim match = Regex.Match(magnetUri, "xt=urn:btih:([a-fA-F0-9]+)", RegexOptions.IgnoreCase)
        If match.Success Then Return match.Groups(1).Value
        Return ""
    End Function

    Private Shared Function ExtractDisplayName(magnetUri As String) As String
        Dim match = Regex.Match(magnetUri, "dn=([^&]+)", RegexOptions.IgnoreCase)
        If match.Success Then Return Uri.UnescapeDataString(match.Groups(1).Value)
        Return ""
    End Function

    Private Shared Function ExtractMagnetValues(magnetUri As String, key As String) As List(Of String)
        Dim values As New List(Of String)()
        Dim pattern = String.Format("(?i)(?:\?|&){0}=([^&]+)", Regex.Escape(key))
        For Each m As Match In Regex.Matches(magnetUri, pattern)
            values.Add(Uri.UnescapeDataString(m.Groups(1).Value.Replace("+", "%20")))
        Next
        Return values
    End Function

    Private Shared Function BuildSocketJackMagnet(metadata As TorrentMetadata) As String
        Dim parts As New List(Of String) From {
            "xt=urn:btih:" & Uri.EscapeDataString(metadata.InfoHash),
            "dn=" & Uri.EscapeDataString(metadata.Name),
            "xl=" & metadata.TotalSize.ToString(),
            "x.sjps=" & metadata.PieceSize.ToString(),
            "x.sjpc=" & metadata.PieceCount.ToString(),
            "x.sjph=" & Uri.EscapeDataString(String.Join(",", metadata.PieceHashes))
        }

        For Each tracker In metadata.Trackers
            parts.Add("tr=" & Uri.EscapeDataString(tracker))
        Next

        Return "magnet:?" & String.Join("&", parts)
    End Function

    Private Shared Function TryCreateSocketJackMetadataFromMagnet(magnetUri As String, ByRef metadata As TorrentMetadata) As Boolean
        metadata = Nothing

        Dim pieceHashesValue = ExtractMagnetValues(magnetUri, "x.sjph").FirstOrDefault()
        If String.IsNullOrWhiteSpace(pieceHashesValue) Then Return False

        Dim name = ExtractDisplayName(magnetUri)
        If String.IsNullOrWhiteSpace(name) Then name = "Unknown"

        Dim infoHash = ExtractInfoHash(magnetUri)
        Dim pieceSizeValue = ExtractMagnetValues(magnetUri, "x.sjps").FirstOrDefault()
        Dim totalSizeValue = ExtractMagnetValues(magnetUri, "xl").FirstOrDefault()
        Dim pieceCountValue = ExtractMagnetValues(magnetUri, "x.sjpc").FirstOrDefault()
        Dim trackers = ExtractMagnetValues(magnetUri, "tr")

        Dim pieceSize As Integer = 0
        Dim totalSize As Long = 0
        Dim pieceCount As Integer = 0

        If Not Integer.TryParse(pieceSizeValue, pieceSize) OrElse pieceSize <= 0 Then Return False
        If Not Long.TryParse(totalSizeValue, totalSize) OrElse totalSize <= 0 Then Return False

        Dim pieceHashes = pieceHashesValue.Split(","c).Where(Function(h) Not String.IsNullOrWhiteSpace(h)).ToList()
        If pieceHashes.Count = 0 Then Return False

        If Not Integer.TryParse(pieceCountValue, pieceCount) OrElse pieceCount <= 0 Then
            pieceCount = pieceHashes.Count
        End If

        If String.IsNullOrWhiteSpace(infoHash) Then Return False
        If trackers.Count = 0 Then trackers.Add("127.0.0.1:7700")

        metadata = New TorrentMetadata With {
            .Name = name,
            .InfoHash = infoHash,
            .PieceSize = pieceSize,
            .TotalSize = totalSize,
            .PieceCount = pieceCount,
            .PieceHashes = pieceHashes,
            .Trackers = trackers
        }
        Return True
    End Function

    Private Async Function StartDownloadFromMetadataAsync(transfer As TransferItem, metadata As TorrentMetadata) As Task
        Try
            Dim tempDir = Path.Combine(Path.GetTempPath(), "SocketJack_Torrent")
            If Not Directory.Exists(tempDir) Then Directory.CreateDirectory(tempDir)
            _downloadDir = Path.Combine(tempDir, "downloads")
            If Not Directory.Exists(_downloadDir) Then Directory.CreateDirectory(_downloadDir)

            Dim listenPort = NIC.FindOpenPort(7900, 8100).Result
            Dim client As New TorrentClient(metadata, listenPort, _downloadDir)
            transfer.Client = client
            transfer.Metadata = metadata

            AddHandler client.PieceCompleted, Sub(pieceIndex)
                                                  Dispatcher.InvokeAsync(Sub()
                                                                             transfer.ProgressValue = client.Progress
                                                                             transfer.Status = "Downloading"
                                                                         End Sub)
                                              End Sub
            AddHandler client.DownloadCompleted, Sub()
                                                     Dispatcher.InvokeAsync(Sub()
                                                                                transfer.ProgressValue = 100
                                                                                transfer.Status = "Complete"
                                                                                transfer.Speed = "--"
                                                                                transfer.ETA = "--"
                                                                            End Sub)
                                                 End Sub
            AddHandler client.PeerConnectedEvent, Sub(peerId)
                                                      Dim shortId = If(peerId.Length > 8, peerId.Substring(0, 8), peerId)
                                                      Log("[Downloader] Peer connected: " & shortId)
                                                  End Sub
            AddHandler client.ErrorOccurred, Sub(ex)
                                                 Log("[Downloader] Error: " & ex.Message)
                                                 Dispatcher.InvokeAsync(Sub() transfer.Status = "Error")
                                             End Sub

            Dispatcher.InvokeAsync(Sub() transfer.Status = "Connecting")
            Await client.StartAsync()
            Dispatcher.InvokeAsync(Sub() transfer.Status = "Downloading")
        Catch ex As Exception
            Log("[Downloader] Failed to start: " & ex.Message)
            Dispatcher.InvokeAsync(Sub() transfer.Status = "Failed")
        End Try
    End Function

#End Region

#Region "Seed File"

    Private Async Sub SeedFileAsync(filePath As String)
        Try
            Running = True
            Log("// ====================================")
            Log("//  Seed File")
            Log("// ====================================")
            Log("")

            Dim tempDir = Path.Combine(Path.GetTempPath(), "SocketJack_Torrent")
            If Not Directory.Exists(tempDir) Then Directory.CreateDirectory(tempDir)
            _downloadDir = Path.Combine(tempDir, "downloads")
            If Not Directory.Exists(_downloadDir) Then Directory.CreateDirectory(_downloadDir)

            Log(String.Format("[Setup] Selected file: {0}", Path.GetFileName(filePath)))
            Log(String.Format("[Setup] File size: {0}", FormatBytes(New FileInfo(filePath).Length)))

            Dim pieceSize As Integer = GetPieceSize()
            _trackerPort = NIC.FindOpenPort(7700, 7800).Result
            _seederPort = NIC.FindOpenPort(7800, 7900).Result

            Dim trackerAddr As String = String.Format("127.0.0.1:{0}", _trackerPort)
            _metadata = TorrentMetadata.FromFile(filePath, New List(Of String) From {trackerAddr}, pieceSize)

            Dim transfer As New TransferItem()
            transfer.Name = _metadata.Name
            transfer.Size = FormatBytes(_metadata.TotalSize)
            transfer.ProgressValue = 100
            transfer.Status = "Seeding"
            transfer.IsSeeding = True
            transfer.Metadata = _metadata
            transfer.InfoHash = _metadata.InfoHash
            transfer.MagnetUri = BuildSocketJackMagnet(_metadata)

            Dispatcher.InvokeAsync(Sub()
                                       _transfers.Add(transfer)
                                       UpdateStatusBar()
                                   End Sub)

            Dim useMutable As Boolean = False
            Dispatcher.Invoke(Sub() useMutable = MenuUseMutable.IsChecked)

            If useMutable Then
                Log(String.Format("[MutableServer] Starting on port {0}...", _trackerPort))
                _mutableServer = New MutableTcpServer(_trackerPort, "TorrentMutableServer")
                _mutableServer.EnabledProtocols.UnionWith({MutableTcpProtocols.Http, MutableTcpProtocols.SocketJack})
                _mutableServer.Listen()

                _metadata.Trackers.Clear()
                _metadata.Trackers.Add(trackerAddr)

                _seeder = New TorrentClient(_metadata, downloadPath:=_downloadDir)
                _seeder.Register(_mutableServer)
                _seeder.Seed(filePath)

                If _seeder.Tracker IsNot Nothing Then
                    _seeder.Tracker.RegisterTorrent(_metadata)
                End If
                Dispatcher.InvokeAsync(Sub() StatusTracker.Text = "Tracker: Registered")
            Else
                Log("[Tracker] Starting tracker on port " & _trackerPort & "...")
                _tracker = New TorrentTracker(_trackerPort, "Tracker")
                _tracker.RegisterTorrent(_metadata)
                Dim started = _tracker.Start()
                If Not started Then
                    Log("[Tracker] Failed to start!")
                    Return
                End If

                Log("[Seeder] Starting seeder on port " & _seederPort & "...")
                _seeder = New TorrentClient(_metadata, _seederPort, _downloadDir)
                _seeder.Seed(filePath)
                Dispatcher.InvokeAsync(Sub() StatusTracker.Text = "Tracker: Online")
            End If

            transfer.Client = _seeder

            AddHandler _seeder.PeerConnectedEvent, Sub(peerId)
                                                       Interlocked.Increment(_PeersConnected)
                                                       Log(String.Format("[Seeder] Peer Connected: {0}", peerId.Substring(0, 8)))
                                                       Dispatcher.InvokeAsync(Sub()
                                                                                  transfer.Peers = _PeersConnected.ToString()
                                                                                  UpdateStatusBar()
                                                                              End Sub)
                                                   End Sub
            AddHandler _seeder.ErrorOccurred, Sub(ex) Log(String.Format("[Seeder] Error: {0}", ex.Message))

            Await _seeder.StartAsync()
            Log(String.Format("[Seeder] Seeding {0} ({1} pieces)", _metadata.Name, _metadata.PieceCount))
            Log("[Seeder] Magnet link:")
            Log(transfer.MagnetUri)

        Catch ex As OperationCanceledException
            Log("// Cancelled.")
        Catch ex As Exception
            Log(String.Format("[ERROR] {0}", ex.Message))
            Log(ex.StackTrace)
        End Try
    End Sub

#End Region

#Region "RuTracker Login"

    Private Sub ButtonRuTrackerLogin_Click(sender As Object, e As RoutedEventArgs)
        Dim loginWindow As New RuTrackerLoginWindow()
        loginWindow.Owner = Window.GetWindow(Me)

        If loginWindow.ShowDialog() = True AndAlso loginWindow.SessionCookies IsNot Nothing Then
            ' Inject browser cookies into our CookieContainer
            For Each cookie As System.Net.Cookie In loginWindow.SessionCookies
                _cookieContainer.Add(cookie)
            Next

            ' Check if a session cookie is present
            Dim cookies = _cookieContainer.GetCookies(New Uri("https://rutracker.org"))
            Dim hasSession As Boolean = False
            For Each cookie As System.Net.Cookie In cookies
                If cookie.Name.Contains("bb_session") OrElse cookie.Name.Contains("bb_data") Then
                    hasSession = True
                    Exit For
                End If
            Next

            If hasSession Then
                _ruTrackerLoggedIn = True
                Log("[RuTracker] Login successful (via browser).")
                RuTrackerLoginStatus.Text = "Logged in"
                RuTrackerLoginStatus.Foreground = New Media.SolidColorBrush(Media.Color.FromRgb(&H66, &HCC, &H66))
            Else
                _ruTrackerLoggedIn = False
                Log("[RuTracker] No session cookie found. Try logging in again.")
                RuTrackerLoginStatus.Text = "Login failed"
                RuTrackerLoginStatus.Foreground = New Media.SolidColorBrush(Media.Color.FromRgb(&HCC, &H66, &H66))
            End If
        Else
            Log("[RuTracker] Login cancelled.")
        End If
    End Sub

#End Region

#Region "RuTracker Scraping"

    Private Async Sub SearchRuTracker(query As String, Optional page As Integer = 0)
        Try
            If _ruTrackerSearchCts IsNot Nothing Then
                _ruTrackerSearchCts.Cancel()
                _ruTrackerSearchCts.Dispose()
            End If
            _ruTrackerSearchCts = New CancellationTokenSource()
            Dim ct = _ruTrackerSearchCts.Token

            Dim pageStart As Integer = page * RuTrackerResultsPerPage
            Dispatcher.InvokeAsync(Sub()
                                       StatusSearch.Text = String.Format("Search: Searching (page {0})...", page + 1)
                                       _ruTrackerResults.Clear()
                                   End Sub)

            If Not _ruTrackerLoggedIn Then
                Log("[RuTracker] Not logged in. Please log in first.")
                Dispatcher.InvokeAsync(Sub() StatusSearch.Text = "Search: Login required")
                Return
            End If

            Log(String.Format("[RuTracker] Searching for '{0}' (page {1})...", query, page + 1))

            Dim encodedQuery As String = Uri.EscapeDataString(query)
            Dim url As String = String.Format("https://rutracker.org/forum/tracker.php?nm={0}&start={1}", encodedQuery, pageStart)

            _httpClient.DefaultRequestHeaders.Clear()
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36")
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9")

            Dim html As String
            Try
                Dim response = Await _httpClient.GetAsync(New Uri(url), ct)
                response.EnsureSuccessStatusCode()
                Dim bytes = Await response.Content.ReadAsByteArrayAsync()

                Dim enc As Encoding = Encoding.UTF8
                Dim contentType = response.Content.Headers.ContentType
                If contentType IsNot Nothing AndAlso Not String.IsNullOrEmpty(contentType.CharSet) Then
                    Try
                        enc = Encoding.GetEncoding(contentType.CharSet.Trim("""".ToCharArray()))
                    Catch
                        enc = Encoding.GetEncoding("windows-1251")
                    End Try
                Else
                    enc = Encoding.GetEncoding("windows-1251")
                End If
                html = enc.GetString(bytes)
            Catch ex As System.Net.Http.HttpRequestException
                Log(String.Format("[RuTracker] HTTP error: {0}", ex.Message))
                Dispatcher.InvokeAsync(Sub() StatusSearch.Text = "Search: Error (network)")
                Return
            End Try

            ct.ThrowIfCancellationRequested()

            If html.Contains("login-form-full") OrElse html.Contains("login_username") Then
                _ruTrackerLoggedIn = False
                Log("[RuTracker] Session expired. Please log in again.")
                Dispatcher.InvokeAsync(Sub()
                                           StatusSearch.Text = "Search: Session expired"
                                           RuTrackerLoginStatus.Text = "Session expired"
                                           RuTrackerLoginStatus.Foreground = New Media.SolidColorBrush(Media.Color.FromRgb(&HCC, &H66, &H66))
                                       End Sub)
                Return
            End If

            Dim addedCount As Integer = 0
            Dim topicPattern As String = "(?s)<tr[^>]*class=""tCenter[^""]*hl-tr""[^>]*>.*?</tr>"
            Dim topicMatches = Regex.Matches(html, topicPattern, RegexOptions.IgnoreCase)

            If topicMatches.Count = 0 Then
                Dim simpleMagnets = Regex.Matches(html, "href=""(magnet:\?[^""]+)""", RegexOptions.IgnoreCase)
                For Each m As Match In simpleMagnets
                    ct.ThrowIfCancellationRequested()
                    Dim result As New RuTrackerResult()
                    result.MagnetLink = System.Net.WebUtility.HtmlDecode(m.Groups(1).Value)
                    result.Title = ExtractDisplayName(result.MagnetLink)
                    If String.IsNullOrEmpty(result.Title) Then result.Title = "Unknown torrent"
                    addedCount += 1
                    Dim resultCopy = result
                    Dispatcher.InvokeAsync(Sub()
                                               _ruTrackerResults.Add(resultCopy)
                                               StatusSearch.Text = String.Format("Search: {0} loaded (page {1})", addedCount, page + 1)
                                           End Sub)
                Next
            Else
                For Each topicMatch As Match In topicMatches
                    ct.ThrowIfCancellationRequested()
                    Dim row As String = topicMatch.Value
                    Dim result As New RuTrackerResult()

                    Dim titleMatch = Regex.Match(row, "class=""[^""]*tLink[^""]*""[^>]*>([^<]+)<", RegexOptions.IgnoreCase)
                    If Not titleMatch.Success Then Continue For
                    result.Title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups(1).Value.Trim())

                    Dim topicIdMatch = Regex.Match(row, "viewtopic\.php\?t=(\d+)", RegexOptions.IgnoreCase)
                    If topicIdMatch.Success Then result.TopicId = topicIdMatch.Groups(1).Value

                    Dim sizeMatch = Regex.Match(row, "class=""[^""]*dl-stub[^""]*""[^>]*>([^<]+)<", RegexOptions.IgnoreCase)
                    If sizeMatch.Success Then
                        result.Size = System.Net.WebUtility.HtmlDecode(sizeMatch.Groups(1).Value.Trim())
                    Else
                        Dim sizeMatch2 = Regex.Match(row, "tor-size[^>]*>.*?<u>(\d+)</u>", RegexOptions.IgnoreCase)
                        If sizeMatch2.Success Then
                            Dim sizeBytes As Long = 0
                            If Long.TryParse(sizeMatch2.Groups(1).Value, sizeBytes) Then
                                result.Size = FormatBytes(sizeBytes)
                            End If
                        End If
                    End If

                    Dim seedMatch = Regex.Match(row, "class=""[^""]*seedmed[^""]*""[^>]*>.*?<b>(\d+)</b>", RegexOptions.IgnoreCase)
                    If seedMatch.Success Then result.Seeds = seedMatch.Groups(1).Value

                    Dim leechMatch = Regex.Match(row, "class=""[^""]*leechmed[^""]*""[^>]*>.*?<b>(\d+)</b>", RegexOptions.IgnoreCase)
                    If leechMatch.Success Then result.Leechers = leechMatch.Groups(1).Value

                    Dim magnetMatch = Regex.Match(row, "href=""(magnet:\?[^""]+)""", RegexOptions.IgnoreCase)
                    If magnetMatch.Success Then
                        result.MagnetLink = System.Net.WebUtility.HtmlDecode(magnetMatch.Groups(1).Value)
                    End If

                    If String.IsNullOrWhiteSpace(result.MagnetLink) AndAlso Not String.IsNullOrWhiteSpace(result.TopicId) Then
                        Try
                            Dim topicUrl As String = String.Format("https://rutracker.org/forum/viewtopic.php?t={0}", result.TopicId)
                            Dim topicResp = Await _httpClient.GetAsync(New Uri(topicUrl), ct)
                            If topicResp.IsSuccessStatusCode Then
                                Dim topicBytes = Await topicResp.Content.ReadAsByteArrayAsync()
                                Dim topicHtml = Encoding.GetEncoding("windows-1251").GetString(topicBytes)
                                Dim topicMagnet = Regex.Match(topicHtml, "href=""(magnet:\?[^""]+)""", RegexOptions.IgnoreCase)
                                If topicMagnet.Success Then
                                    result.MagnetLink = System.Net.WebUtility.HtmlDecode(topicMagnet.Groups(1).Value)
                                End If
                            End If
                        Catch ex As OperationCanceledException
                            Throw
                        Catch
                        End Try
                    End If

                    addedCount += 1
                    Dim resultCopy = result
                    Dispatcher.InvokeAsync(Sub()
                                               _ruTrackerResults.Add(resultCopy)
                                               StatusSearch.Text = String.Format("Search: {0} loaded (page {1})", addedCount, page + 1)
                                           End Sub)
                Next
            End If

            Dispatcher.InvokeAsync(Sub()
                                       Log(String.Format("[RuTracker] Loaded {0} result(s) on page {1}.", addedCount, page + 1))
                                       Dim prevBtn = CType(FindName("ButtonRuTrackerPrev"), Button)
                                       Dim nextBtn = CType(FindName("ButtonRuTrackerNext"), Button)
                                       If prevBtn IsNot Nothing Then prevBtn.IsEnabled = (page > 0)
                                       If nextBtn IsNot Nothing Then nextBtn.IsEnabled = (addedCount >= RuTrackerResultsPerPage)
                                       StatusSearch.Text = String.Format("Search: {0} found (page {1})", addedCount, page + 1)
                                   End Sub)
        Catch ex As OperationCanceledException
            Dispatcher.InvokeAsync(Sub() StatusSearch.Text = "Search: Cancelled")
        Catch ex As Exception
            Log(String.Format("[RuTracker] Error: {0}", ex.Message))
            Dispatcher.InvokeAsync(Sub() StatusSearch.Text = "Search: Error")
        End Try
    End Sub

#End Region

#Region "Speed Timer"

    Private Sub SpeedTimerTick(sender As Object, e As EventArgs)
        Dim totalDownloaded As Long = 0
        Dim totalPeers As Integer = 0
        For Each t In _transfers
            If t.Client IsNot Nothing Then
                totalDownloaded += t.Client.BytesDownloaded
                ' Update individual transfer speed
                Dim delta As Long = t.Client.BytesDownloaded - t.LastBytes
                t.LastBytes = t.Client.BytesDownloaded
                If delta > 0 Then
                    t.Speed = FormatBytes(delta) & "/s"
                    If t.Client.BytesRemaining > 0 AndAlso delta > 0 Then
                        Dim etaSeconds As Long = t.Client.BytesRemaining \ delta
                        If etaSeconds < 60 Then
                            t.ETA = String.Format("{0}s", etaSeconds)
                        ElseIf etaSeconds < 3600 Then
                            t.ETA = String.Format("{0}m {1}s", etaSeconds \ 60, etaSeconds Mod 60)
                        Else
                            t.ETA = String.Format("{0}h {1}m", etaSeconds \ 3600, (etaSeconds Mod 3600) \ 60)
                        End If
                    Else
                        t.ETA = "--"
                    End If
                ElseIf Not t.IsSeeding Then
                    t.Speed = "--"
                End If
            ElseIf t.StandardManager IsNot Nothing Then
                Dim mgr = t.StandardManager
                t.ProgressValue = mgr.Progress
                Dim rate As Long = mgr.Monitor.DownloadRate
                totalDownloaded += mgr.Monitor.DataBytesReceived
                If rate > 0 Then
                    t.Speed = FormatBytes(rate) & "/s"
                Else
                    t.Speed = "--"
                End If
                t.Status = mgr.State.ToString()
            End If
        Next

        Dim globalDelta As Long = totalDownloaded - _lastBytesDownloaded
        _lastBytesDownloaded = totalDownloaded
        StatusSpeed.Text = String.Format("? {0}/s", FormatBytes(globalDelta))
    End Sub

#End Region

#Region "UI Helpers"

    Public Sub Log(text As String, Optional AppendNewLine As Boolean = False)
        Dim isAtEnd As Boolean = TextboxLog.VerticalOffset >= (TextboxLog.ExtentHeight - TextboxLog.ViewportHeight) * 0.9
        Dispatcher.InvokeAsync(Sub()
                                   TextboxLog.AppendText(If(TextboxLog.Text = String.Empty, String.Empty, vbCrLf) & text & If(AppendNewLine, Environment.NewLine, String.Empty))
                                   If isAtEnd Then TextboxLog.ScrollToEnd()
                               End Sub)
    End Sub

    Private Sub UpdateStatusBar()
        Dispatcher.InvokeAsync(Sub()
                                   StatusTransfers.Text = String.Format("Transfers: {0}", _transfers.Count)
                                   StatusPeers.Text = String.Format("Peers: {0}", _PeersConnected)
                                   If _transfers.Count > 0 Then
                                       TransferBadgeText.Text = _transfers.Count.ToString()
                                       TransferBadge.Visibility = Visibility.Visible
                                   Else
                                       TransferBadge.Visibility = Visibility.Collapsed
                                   End If
                               End Sub)
    End Sub

    Private Shared Function FormatBytes(bytes As Long) As String
        If bytes < 0 Then Return "0 B"
        If bytes < 1024 Then Return String.Format("{0} B", bytes)
        If bytes < 1024 * 1024 Then Return String.Format("{0:F1} KB", bytes / 1024.0)
        If bytes < 1024 * 1024 * 1024 Then Return String.Format("{0:F1} MB", bytes / (1024.0 * 1024.0))
        Return String.Format("{0:F2} GB", bytes / (1024.0 * 1024.0 * 1024.0))
    End Function

#End Region

#Region "Lifecycle"

    Private Sub ITest_StartTest() Implements ITest.StartTest
        Log("// Use File > Seed File or File > Add Magnet Link to begin.")
    End Sub

    Private Sub ITest_StopTest() Implements ITest.StopTest
        StopAll()
    End Sub

    Private Sub StopAll()
        Running = False
        If _cts IsNot Nothing Then _cts.Cancel()
        Cleanup()
        Dispatcher.InvokeAsync(Sub()
                                   StatusTracker.Text = "Tracker: Offline"
                                   For Each t In _transfers
                                       If t.Status <> "Complete" Then
                                           t.Status = "Stopped"
                                       End If
                                   Next
                                   UpdateStatusBar()
                               End Sub)
        Log("Stopped.")
    End Sub

    Private Sub Cleanup()
        If _standardBtEngine IsNot Nothing Then
            Try
                _standardBtEngine.StopAllAsync().Wait(TimeSpan.FromSeconds(5))
                _standardBtEngine.Dispose()
            Catch
            End Try
            _standardBtEngine = Nothing
        End If

        If _seeder IsNot Nothing Then
            _seeder.Dispose()
            _seeder = Nothing
        End If
        If _tracker IsNot Nothing Then
            _tracker.Dispose()
            _tracker = Nothing
        End If
        If _mutableServer IsNot Nothing Then
            _mutableServer.Dispose()
            _mutableServer = Nothing
        End If
        _PeersConnected = 0
        SyncLock _peerIds
            _peerIds.Clear()
        End SyncLock
    End Sub

#End Region

#Region "Helpers"

    Private Function GetPieceSize() As Integer
        Dim value As Integer = DefaultPieceSize
        Dispatcher.Invoke(Sub()
                              Dim text As String = TextBoxPieceSize.Text
                              Dim kb As Integer = 0
                              If Integer.TryParse(text, kb) AndAlso kb > 0 Then
                                  value = kb * 1024
                              End If
                          End Sub)
        Return value
    End Function

#End Region

End Class

