Imports System.ComponentModel
Imports System.IO
Imports System.Linq
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Controls.Primitives
Imports System.Windows.Media
Imports System.Windows.Media.Imaging
Imports System.Windows.Threading
Imports TagLibNamespace = TagLib
Imports WinForms = System.Windows.Forms

Class MainWindow
    Public Class TrackEntry
        Implements INotifyPropertyChanged

        Public Property Number As Integer
        Public Property Path As String
        Public Property Title As String
        Public Property Artist As String
        Public Property Album As String
        Public Property Duration As String
        Public Property DurationSeconds As Double
        Public Property LastModifiedUtc As DateTime
        Public Property CoverImage As ImageSource
        Private _isFavourite As Boolean

        Public Property IsFavourite As Boolean
            Get
                Return _isFavourite
            End Get
            Set(value As Boolean)
                If _isFavourite = value Then Return
                _isFavourite = value
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(IsFavourite)))
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(FavouriteGlyph)))
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(FavouriteToolTip)))
            End Set
        End Property

        Public ReadOnly Property FavouriteGlyph As String
            Get
                Return If(IsFavourite, ChrW(&HEB52), ChrW(&HEB51))
            End Get
        End Property

        Public ReadOnly Property FavouriteToolTip As String
            Get
                Return If(IsFavourite, "Remove from Favourites", "Save to Favourites")
            End Get
        End Property

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
    End Class

    Public Class PlayerSettings
        Public Property MusicFolder As String = ""
        Public Property Volume As Double = 0.8
        Public Property Favourites As List(Of String) = New List(Of String)
        Public Property Shuffle As Boolean
        Public Property Repeat As Integer
        Public Property LibraryView As String = "Browse"
    End Class

    Private Class LibraryScanResult
        Public Property Tracks As New List(Of TrackEntry)
        Public Property ErrorMessage As String = ""
    End Class

    Private ReadOnly _settingsPath As String = IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "music-player", "settings.json")
    Private ReadOnly _random As New Random()
    Private _settings As New PlayerSettings()
    Private _favourites As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private _libraryEntries As New List(Of TrackEntry)
    Private _trackCache As New Dictionary(Of String, TrackEntry)(StringComparer.OrdinalIgnoreCase)
    Private _displayedTrackEntries As New List(Of TrackEntry)
    Private _queueSource As New List(Of String)
    Private _playbackOrder As New List(Of String)
    Private _queueIndex As Integer = -1
    Private _currentTrackPath As String = ""
    Private _currentFolder As String = ""
    Private _currentMode As String = "Browse"
    Private _isPlaying As Boolean
    Private _shuffle As Boolean
    Private _repeat As Integer ' 0 = off, 1 = repeat queue, 2 = repeat track.
    Private _isInitialized As Boolean
    Private _isClosed As Boolean
    Private _isUpdatingSelection As Boolean
    Private _isUpdatingProgress As Boolean
    Private _isSeeking As Boolean
    Private _isLoading As Boolean
    Private _lastAudibleVolume As Double = 0.8
    Private _positionTimer As DispatcherTimer
    Private _settingsTimer As DispatcherTimer
    Private _scanCancellation As CancellationTokenSource

    Private Async Sub Window_Loaded(sender As Object, e As RoutedEventArgs)
        If _isInitialized Then Return
        InitializePlayer()
        Dim folder = _settings.MusicFolder
        If String.IsNullOrWhiteSpace(folder) OrElse Not Directory.Exists(folder) Then folder = GetDefaultMusicFolder()
        Await LoadMusicLibraryAsync(folder)
    End Sub

    Private Sub InitializePlayer()
        If _isInitialized Then Return
        UserNameText.Text = Environment.UserName
        _settings = ReadSettings()
        _favourites = New HashSet(Of String)(If(_settings.Favourites, New List(Of String)), StringComparer.OrdinalIgnoreCase)
        _shuffle = _settings.Shuffle
        _repeat = Math.Clamp(_settings.Repeat, 0, 2)
        _currentMode = If({"Browse", "Recently Added", "Favourites"}.Contains(_settings.LibraryView), _settings.LibraryView, "Browse")
        Dim volume = If(Double.IsNaN(_settings.Volume) OrElse Double.IsInfinity(_settings.Volume), 0.8, Math.Clamp(_settings.Volume, 0.0, 1.0))
        VolumeSlider.Value = volume
        AudioPlayer.Volume = volume
        If volume > 0 Then _lastAudibleVolume = volume
        InitializeProgressTimer()
        _settingsTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(500)}
        AddHandler _settingsTimer.Tick, AddressOf SettingsTimer_Tick
        AddHandler AudioPlayer.MediaFailed, AddressOf AudioPlayer_MediaFailed
        SongProgressSlider.AddHandler(Thumb.DragStartedEvent, New DragStartedEventHandler(AddressOf ProgressSlider_DragStarted), True)
        SongProgressSlider.AddHandler(Thumb.DragCompletedEvent, New DragCompletedEventHandler(AddressOf ProgressSlider_DragCompleted), True)
        AddHandler SongProgressSlider.LostMouseCapture, AddressOf ProgressSlider_LostMouseCapture
        AddHandler Closed, AddressOf Window_Closed
        _isInitialized = True
        UpdateVolumeButton()
        ResetTrackInfo("Ready to listen", "Your local music, all in one place")
        UpdatePlaybackControls()
    End Sub

    Private Sub InitializeProgressTimer()
        If _positionTimer IsNot Nothing Then Return
        _positionTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(250)}
        AddHandler _positionTimer.Tick, AddressOf PositionTimer_Tick
    End Sub

    Private Sub BrowseButton_Click(sender As Object, e As RoutedEventArgs)
        ChangeLibraryView("Browse")
    End Sub

    Private Sub RecentlyAddedButton_Click(sender As Object, e As RoutedEventArgs)
        ChangeLibraryView("Recently Added")
    End Sub

    Private Sub FavouritesButton_Click(sender As Object, e As RoutedEventArgs)
        ChangeLibraryView("Favourites")
    End Sub

    Private Sub ChangeLibraryView(mode As String)
        _currentMode = mode
        If Not _isInitialized Then Return
        RefreshVisibleTracks()
        ScheduleSaveSettings()
    End Sub

    Private Sub PlayMixButton_Click(sender As Object, e As RoutedEventArgs)
        If _displayedTrackEntries.Count > 0 Then BeginPlaybackQueue(_displayedTrackEntries(0).Path)
    End Sub

    Private Sub PlayPauseButton_Click(sender As Object, e As RoutedEventArgs)
        If Not _isInitialized Then Return
        If AudioPlayer.Source Is Nothing Then
            If _displayedTrackEntries.Count > 0 Then BeginPlaybackQueue(_displayedTrackEntries(0).Path)
            Return
        End If
        _isPlaying = Not _isPlaying
        If _isPlaying Then
            If AudioPlayer.NaturalDuration.HasTimeSpan AndAlso AudioPlayer.Position >= AudioPlayer.NaturalDuration.TimeSpan Then AudioPlayer.Position = TimeSpan.Zero
            AudioPlayer.Play()
            _positionTimer.Start()
        Else
            AudioPlayer.Pause()
            _positionTimer.Stop()
        End If
        UpdatePlaybackControls()
    End Sub

    Private Sub PreviousTrackButton_Click(sender As Object, e As RoutedEventArgs)
        If Not _isInitialized OrElse _playbackOrder.Count = 0 Then Return
        If AudioPlayer.Position.TotalSeconds > 3 Then
            AudioPlayer.Position = TimeSpan.Zero
            SetProgress(0)
            Return
        End If
        _queueIndex = If(_queueIndex <= 0, _playbackOrder.Count - 1, _queueIndex - 1)
        PlayTrack(_playbackOrder(_queueIndex))
    End Sub

    Private Sub NextTrackButton_Click(sender As Object, e As RoutedEventArgs)
        AdvanceTrack(False)
    End Sub

    Private Sub AdvanceTrack(endedNaturally As Boolean)
        If Not _isInitialized OrElse _playbackOrder.Count = 0 Then Return
        If endedNaturally AndAlso _repeat = 2 Then
            AudioPlayer.Position = TimeSpan.Zero
            AudioPlayer.Play()
            _positionTimer.Start()
            Return
        End If
        If _queueIndex + 1 >= _playbackOrder.Count Then
            If endedNaturally AndAlso _repeat = 0 Then
                _isPlaying = False
                _positionTimer.Stop()
                UpdatePlaybackControls()
                PlayStateText.Text = "Queue finished"
                PlayerStatusText.Text = "Queue finished · play something else"
                UpdateNextQueue()
                Return
            End If
            _queueIndex = 0
        Else
            _queueIndex += 1
        End If
        PlayTrack(_playbackOrder(_queueIndex))
    End Sub

    Private Sub SongsListBox_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
        If Not _isInitialized OrElse _isUpdatingSelection Then Return
        Dim entry = TryCast(SongsListBox.SelectedItem, TrackEntry)
        If entry IsNot Nothing Then BeginPlaybackQueue(entry.Path)
    End Sub

    Private Sub BeginPlaybackQueue(trackPath As String)
        _queueSource = _displayedTrackEntries.Select(Function(track) track.Path).ToList()
        If _queueSource.Count = 0 Then Return
        RebuildPlaybackOrder(trackPath)
        PlayTrack(trackPath)
    End Sub

    Private Sub RebuildPlaybackOrder(trackPath As String)
        _playbackOrder = _queueSource.ToList()
        If _shuffle AndAlso _playbackOrder.Count > 1 Then
            _playbackOrder.RemoveAll(Function(path) String.Equals(path, trackPath, StringComparison.OrdinalIgnoreCase))
            For index = _playbackOrder.Count - 1 To 1 Step -1
                Dim swapIndex = _random.Next(index + 1)
                Dim value = _playbackOrder(index)
                _playbackOrder(index) = _playbackOrder(swapIndex)
                _playbackOrder(swapIndex) = value
            Next
            If _trackCache.ContainsKey(trackPath) Then _playbackOrder.Insert(0, trackPath)
        End If
        _queueIndex = _playbackOrder.FindIndex(Function(path) String.Equals(path, trackPath, StringComparison.OrdinalIgnoreCase))
        UpdateNextQueue()
    End Sub

    Private Async Sub ChooseFolderButton_Click(sender As Object, e As RoutedEventArgs)
        Using dialog As New WinForms.FolderBrowserDialog()
            dialog.Description = "Choose the folder where you keep your music"
            dialog.UseDescriptionForTitle = True
            If Directory.Exists(_currentFolder) Then dialog.SelectedPath = _currentFolder
            If dialog.ShowDialog() = WinForms.DialogResult.OK Then Await LoadMusicLibraryAsync(dialog.SelectedPath)
        End Using
    End Sub

    Private Async Function LoadMusicLibraryAsync(folderPath As String) As Task
        If Not _isInitialized Then InitializePlayer()
        If _scanCancellation IsNot Nothing Then _scanCancellation.Cancel()
        Dim cancellation As New CancellationTokenSource()
        _scanCancellation = cancellation
        Dim token = cancellation.Token
        _currentFolder = folderPath
        _isLoading = True
        CollectionPlayButton.IsEnabled = False
        EmptyLibraryActionButton.IsEnabled = False
        FolderPathText.Text = folderPath
        FolderPathText.ToolTip = folderPath
        PlayerStatusText.Text = "Finding your music…"
        LibraryCountText.Text = "Scanning your music folder…"
        SongsListBox.IsEnabled = False
        EmptyLibraryPanel.Visibility = Visibility.Visible
        EmptyLibraryTitle.Text = "Making room for your music"
        EmptyLibraryDescription.Text = "Reading your tracks and album artwork…"
        Try
            Dim result = Await Task.Run(Function() ScanLibrary(folderPath, token), token)
            If token.IsCancellationRequested OrElse _isClosed Then Return
            AudioPlayer.Stop()
            AudioPlayer.Source = Nothing
            _positionTimer.Stop()
            _isPlaying = False
            _currentTrackPath = ""
            _queueIndex = -1
            _queueSource.Clear()
            _playbackOrder.Clear()
            _libraryEntries = result.Tracks
            _trackCache = _libraryEntries.ToDictionary(Function(track) track.Path, StringComparer.OrdinalIgnoreCase)
            For Each track In _libraryEntries
                track.IsFavourite = _favourites.Contains(track.Path)
            Next
            _isLoading = False
            RefreshVisibleTracks()
            If _libraryEntries.Count > 0 Then
                _queueSource = _displayedTrackEntries.Select(Function(track) track.Path).ToList()
                If _queueSource.Count = 0 Then _queueSource = _libraryEntries.Select(Function(track) track.Path).ToList()
                RebuildPlaybackOrder(_queueSource(0))
                PlayTrack(_queueSource(0), False)
            Else
                ResetTrackInfo("Ready to listen", "Add a folder to get started")
                SetProgress(0, 1)
                DurationText.Text = "0:00"
                UpdatePlaybackControls()
                UpdateNextQueue()
            End If
            If Not String.IsNullOrWhiteSpace(result.ErrorMessage) Then
                PlayerStatusText.Text = result.ErrorMessage
                EmptyLibraryTitle.Text = "This folder is unavailable"
                EmptyLibraryDescription.Text = "Choose another folder, or check that this drive is connected."
            End If
            ScheduleSaveSettings()
        Catch ex As OperationCanceledException
            ' A newer folder selection owns the view now.
        Catch ex As Exception
            If Not token.IsCancellationRequested Then
                _isLoading = False
                PlayerStatusText.Text = "Couldn’t load this folder. Please choose another one."
                EmptyLibraryTitle.Text = "Your collection couldn’t be loaded"
                EmptyLibraryDescription.Text = "Check the folder permissions and try again."
                EmptyLibraryPanel.Visibility = Visibility.Visible
            End If
        Finally
            If _scanCancellation Is cancellation Then
                _isLoading = False
                SongsListBox.IsEnabled = True
                _scanCancellation = Nothing
                EmptyLibraryActionButton.IsEnabled = True
                UpdateControlAvailability()
            End If
            cancellation.Dispose()
        End Try
    End Function

    Private Shared Function ScanLibrary(folderPath As String, token As CancellationToken) As LibraryScanResult
        Dim result As New LibraryScanResult()
        If String.IsNullOrWhiteSpace(folderPath) OrElse Not Directory.Exists(folderPath) Then
            result.ErrorMessage = "Choose a music folder to get started."
            Return result
        End If
        Try
            Dim options As New EnumerationOptions With {
                .RecurseSubdirectories = True,
                .IgnoreInaccessible = True,
                .AttributesToSkip = FileAttributes.ReparsePoint Or FileAttributes.System
            }
            For Each filePath In Directory.EnumerateFiles(folderPath, "*", options)
                token.ThrowIfCancellationRequested()
                If IsSupportedAudioFile(filePath) Then result.Tracks.Add(ReadTrackEntry(filePath))
            Next
            result.Tracks = result.Tracks.OrderBy(Function(track) track.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(Function(track) track.Artist, StringComparer.CurrentCultureIgnoreCase).ToList()
        Catch ex As OperationCanceledException
            Throw
        Catch ex As IOException
            result.ErrorMessage = "Some music couldn’t be read. Check that your drive is connected."
        Catch ex As UnauthorizedAccessException
            result.ErrorMessage = "Some folders are inaccessible. Choose a folder you have permission to read."
        End Try
        Return result
    End Function

    Private Sub RefreshVisibleTracks()
        If Not _isInitialized OrElse _isLoading Then Return
        Dim source As IEnumerable(Of TrackEntry) = _libraryEntries
        Select Case _currentMode
            Case "Recently Added"
                source = source.OrderByDescending(Function(track) track.LastModifiedUtc)
                LibraryPageTitle.Text = "New additions"
            Case "Favourites"
                source = source.Where(Function(track) track.IsFavourite)
                LibraryPageTitle.Text = "Favourites"
            Case Else
                LibraryPageTitle.Text = "Your collection"
        End Select
        Dim searchText = SearchBox.Text.Trim()
        If searchText.Length > 0 Then
            source = source.Where(Function(track) {track.Title, track.Artist, track.Album}.Any(Function(value) value.IndexOf(searchText, StringComparison.CurrentCultureIgnoreCase) >= 0))
        End If
        _displayedTrackEntries = source.ToList()
        For index = 0 To _displayedTrackEntries.Count - 1
            _displayedTrackEntries(index).Number = index + 1
        Next
        _isUpdatingSelection = True
        Try
            SongsListBox.ItemsSource = _displayedTrackEntries
            SongsListBox.SelectedItem = _displayedTrackEntries.FirstOrDefault(Function(track) String.Equals(track.Path, _currentTrackPath, StringComparison.OrdinalIgnoreCase))
        Finally
            _isUpdatingSelection = False
        End Try
        Dim trackCount = _displayedTrackEntries.Count
        LibraryCountText.Text = If(trackCount = 1, "1 song", $"{trackCount:N0} songs")
        If searchText.Length > 0 Then LibraryCountText.Text &= $" matching '{searchText}'"
        SidebarTrackCountText.Text = If(_libraryEntries.Count = 1, "1 song in your collection", $"{_libraryEntries.Count:N0} songs in your collection")
        Dim totalTime = TimeSpan.FromSeconds(_displayedTrackEntries.Sum(Function(track) track.DurationSeconds))
        LibrarySummaryText.Text = If(totalTime.TotalMinutes >= 1, $"{LibraryCountText.Text} · {FormatLibraryDuration(totalTime)}", LibraryCountText.Text)
        EmptyLibraryPanel.Visibility = If(trackCount = 0, Visibility.Visible, Visibility.Collapsed)
        If searchText.Length > 0 Then
            EmptyLibraryTitle.Text = "No results found"
            EmptyLibraryDescription.Text = "Try a different song, artist, or album name."
            EmptyLibraryActionButton.Content = "Clear search"
        ElseIf _currentMode = "Favourites" AndAlso _libraryEntries.Count > 0 Then
            EmptyLibraryTitle.Text = "Songs you love live here"
            EmptyLibraryDescription.Text = "Tap the heart beside any song to add it to Favourites."
            EmptyLibraryActionButton.Content = "See all music"
        Else
            EmptyLibraryTitle.Text = "Your next favourite is waiting"
            EmptyLibraryDescription.Text = "Choose a folder with MP3, WAV, FLAC, M4A, AAC, or WMA files."
            EmptyLibraryActionButton.Content = "Choose a music folder"
        End If
        ApplyNavSelection()
        UpdateControlAvailability()
    End Sub

    Private Sub ApplyNavSelection()
        For Each button In {BrowseButton, RecentlyAddedButton, FavouritesButton}
            Dim selected = (button Is BrowseButton AndAlso _currentMode = "Browse") OrElse (button Is RecentlyAddedButton AndAlso _currentMode = "Recently Added") OrElse (button Is FavouritesButton AndAlso _currentMode = "Favourites")
            button.Background = If(selected, GetThemeBrush("SecondaryContainerBrush", Color.FromRgb(74, 68, 88)), Brushes.Transparent)
            button.Foreground = If(selected, GetThemeBrush("OnSecondaryContainerBrush", Color.FromRgb(232, 222, 248)), GetThemeBrush("MutedBrush", Color.FromRgb(202, 196, 208)))
        Next
    End Sub

    Private Shared Function GetDefaultMusicFolder() As String
        Dim folder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        Return If(String.IsNullOrWhiteSpace(folder), IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music"), folder)
    End Function

    Private Shared Function ReadTrackEntry(filePath As String) As TrackEntry
        Dim entry As New TrackEntry With {
            .Path = filePath,
            .Title = IO.Path.GetFileNameWithoutExtension(filePath),
            .Artist = "Unknown artist",
            .Album = "Local files",
            .Duration = "—"
        }
        Try
            entry.LastModifiedUtc = File.GetLastWriteTimeUtc(filePath)
            Using tagFile As TagLibNamespace.File = TagLibNamespace.File.Create(filePath)
                If Not String.IsNullOrWhiteSpace(tagFile.Tag.Title) Then entry.Title = tagFile.Tag.Title.Trim()
                If tagFile.Tag.Performers IsNot Nothing AndAlso tagFile.Tag.Performers.Length > 0 Then
                    Dim performers = tagFile.Tag.Performers.Where(Function(artist) Not String.IsNullOrWhiteSpace(artist)).ToArray()
                    If performers.Length > 0 Then entry.Artist = String.Join(", ", performers)
                End If
                If Not String.IsNullOrWhiteSpace(tagFile.Tag.Album) Then entry.Album = tagFile.Tag.Album.Trim()
                If tagFile.Properties IsNot Nothing AndAlso tagFile.Properties.Duration.TotalSeconds > 0 Then
                    entry.DurationSeconds = tagFile.Properties.Duration.TotalSeconds
                    entry.Duration = FormatTime(tagFile.Properties.Duration)
                End If
                If tagFile.Tag.Pictures IsNot Nothing AndAlso tagFile.Tag.Pictures.Length > 0 Then entry.CoverImage = CreateBitmapFromBytes(tagFile.Tag.Pictures(0).Data.Data)
            End Using
        Catch ex As Exception
            ' Untagged or damaged files remain visible with their filename.
        End Try
        Return entry
    End Function

    Private Shared Function CreateBitmapFromBytes(imageBytes As Byte()) As BitmapImage
        If imageBytes Is Nothing OrElse imageBytes.Length = 0 Then Return Nothing
        Try
            Using stream As New MemoryStream(imageBytes)
                Dim result As New BitmapImage()
                result.BeginInit()
                result.CacheOption = BitmapCacheOption.OnLoad
                result.DecodePixelWidth = 512
                result.StreamSource = stream
                result.EndInit()
                result.Freeze()
                Return result
            End Using
        Catch ex As Exception
            Return Nothing
        End Try
    End Function

    Private Sub UpdateNextQueue()
        Dim entries As New List(Of TrackEntry)
        If _playbackOrder.Count > 0 Then
            Dim start = If(_queueIndex >= 0, _queueIndex + 1, 0)
            For offset = 0 To _playbackOrder.Count - 1
                Dim index = start + offset
                If index >= _playbackOrder.Count Then
                    If _repeat <> 1 Then Exit For
                    index = index Mod _playbackOrder.Count
                End If
                If index = _queueIndex Then Exit For
                Dim track As TrackEntry = Nothing
                If _trackCache.TryGetValue(_playbackOrder(index), track) Then entries.Add(track)
            Next
        End If
        NextTrackQueue.ItemsSource = entries.Take(6).ToList()
        QueueCountText.Text = If(entries.Count = 1, "1 song next", $"{entries.Count:N0} songs next")
    End Sub

    Private Sub PlayTrack(trackPath As String, Optional autoplay As Boolean = True)
        Dim entry As TrackEntry = Nothing
        If Not _trackCache.TryGetValue(trackPath, entry) Then Return
        If Not File.Exists(trackPath) Then
            _isPlaying = False
            _positionTimer.Stop()
            AudioPlayer.Pause()
            UpdatePlaybackControls()
            PlayerStatusText.Text = "This file has moved or is unavailable. Choose another song."
            Return
        End If
        Try
            _currentTrackPath = trackPath
            _queueIndex = _playbackOrder.FindIndex(Function(path) String.Equals(path, trackPath, StringComparison.OrdinalIgnoreCase))
            AudioPlayer.Stop()
            AudioPlayer.Source = New Uri(trackPath, UriKind.Absolute)
            SetProgress(0, Math.Max(1, entry.DurationSeconds))
            DurationText.Text = If(entry.DurationSeconds > 0, entry.Duration, "0:00")
            _isPlaying = autoplay
            If autoplay Then
                AudioPlayer.Play()
                _positionTimer.Start()
            Else
                AudioPlayer.Pause()
                _positionTimer.Stop()
            End If
            ApplyTrackMetadata(entry)
            _isUpdatingSelection = True
            Try
                SongsListBox.SelectedItem = _displayedTrackEntries.FirstOrDefault(Function(track) String.Equals(track.Path, trackPath, StringComparison.OrdinalIgnoreCase))
                If SongsListBox.SelectedItem IsNot Nothing Then SongsListBox.ScrollIntoView(SongsListBox.SelectedItem)
            Finally
                _isUpdatingSelection = False
            End Try
            UpdatePlaybackControls()
            UpdateNextQueue()
        Catch ex As Exception
            _isPlaying = False
            _positionTimer.Stop()
            UpdatePlaybackControls()
            PlayerStatusText.Text = "This song couldn’t be played. Try another file."
        End Try
    End Sub

    Private Sub ApplyTrackMetadata(entry As TrackEntry)
        TrackTitleText.Text = entry.Title
        TrackArtistText.Text = entry.Artist
        CurrentTrackTitleText.Text = entry.Title
        CurrentTrackArtistText.Text = entry.Artist
        CurrentAlbumText.Text = entry.Album
        AlbumArtImage.Source = entry.CoverImage
        MiniAlbumArtImage.Source = entry.CoverImage
        UpdateFavouriteButton()
    End Sub

    Private Sub ResetTrackInfo(title As String, artist As String)
        TrackTitleText.Text = title
        TrackArtistText.Text = artist
        CurrentTrackTitleText.Text = title
        CurrentTrackArtistText.Text = artist
        CurrentAlbumText.Text = "Your personal music collection"
        AlbumArtImage.Source = Nothing
        MiniAlbumArtImage.Source = Nothing
        UpdateFavouriteButton()
    End Sub

    Private Sub AudioPlayer_MediaOpened(sender As Object, e As RoutedEventArgs)
        If Not _isInitialized Then Return
        If AudioPlayer.NaturalDuration.HasTimeSpan Then
            SetProgress(AudioPlayer.Position.TotalSeconds, Math.Max(1, AudioPlayer.NaturalDuration.TimeSpan.TotalSeconds))
            DurationText.Text = FormatTime(AudioPlayer.NaturalDuration.TimeSpan)
        End If
        If _isPlaying Then _positionTimer.Start()
    End Sub

    Private Sub AudioPlayer_MediaEnded(sender As Object, e As RoutedEventArgs)
        If Not _isInitialized Then Return
        _positionTimer.Stop()
        AdvanceTrack(True)
    End Sub

    Private Sub AudioPlayer_MediaFailed(sender As Object, e As ExceptionRoutedEventArgs)
        _isPlaying = False
        If _positionTimer IsNot Nothing Then _positionTimer.Stop()
        UpdatePlaybackControls()
        PlayStateText.Text = "Unable to play"
        PlayerStatusText.Text = "This file couldn’t be played. Try another song or a different audio format."
    End Sub

    Private Sub PositionTimer_Tick(sender As Object, e As EventArgs)
        If AudioPlayer.Source Is Nothing OrElse _isSeeking Then Return
        If AudioPlayer.NaturalDuration.HasTimeSpan Then SetProgress(AudioPlayer.Position.TotalSeconds, AudioPlayer.NaturalDuration.TimeSpan.TotalSeconds)
    End Sub

    Private Sub SetProgress(seconds As Double, Optional maximum As Double = -1)
        _isUpdatingProgress = True
        Try
            If maximum > 0 Then SongProgressSlider.Maximum = maximum
            SongProgressSlider.Value = Math.Clamp(seconds, 0, SongProgressSlider.Maximum)
            CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(Math.Max(0, seconds)))
        Finally
            _isUpdatingProgress = False
        End Try
    End Sub

    Private Sub SongProgressSlider_ValueChanged(sender As Object, e As RoutedPropertyChangedEventArgs(Of Double))
        If Not _isInitialized OrElse _isUpdatingProgress OrElse AudioPlayer Is Nothing OrElse AudioPlayer.Source Is Nothing Then Return
        CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(SongProgressSlider.Value))
        If Not _isSeeking Then SeekToSlider()
    End Sub

    Private Sub ProgressSlider_DragStarted(sender As Object, e As DragStartedEventArgs)
        If AudioPlayer.Source IsNot Nothing Then _isSeeking = True
    End Sub

    Private Sub ProgressSlider_DragCompleted(sender As Object, e As DragCompletedEventArgs)
        CompleteSeek()
    End Sub

    Private Sub ProgressSlider_LostMouseCapture(sender As Object, e As Input.MouseEventArgs)
        CompleteSeek()
    End Sub

    Private Sub CompleteSeek()
        If Not _isSeeking Then Return
        _isSeeking = False
        SeekToSlider()
    End Sub

    Private Sub SeekToSlider()
        If AudioPlayer.Source Is Nothing OrElse Not AudioPlayer.NaturalDuration.HasTimeSpan Then Return
        Try
            AudioPlayer.Position = TimeSpan.FromSeconds(Math.Clamp(SongProgressSlider.Value, 0, AudioPlayer.NaturalDuration.TimeSpan.TotalSeconds))
        Catch ex As InvalidOperationException
            PlayerStatusText.Text = "Wait for this song to finish loading before seeking."
        End Try
    End Sub

    Private Sub ShuffleButton_Click(sender As Object, e As RoutedEventArgs)
        _shuffle = Not _shuffle
        RebuildPlaybackOrder(_currentTrackPath)
        UpdatePlaybackControls()
        ScheduleSaveSettings()
    End Sub

    Private Sub RepeatButton_Click(sender As Object, e As RoutedEventArgs)
        _repeat = (_repeat + 1) Mod 3
        UpdatePlaybackControls()
        UpdateNextQueue()
        ScheduleSaveSettings()
    End Sub

    Private Sub FavouriteButton_Click(sender As Object, e As RoutedEventArgs)
        ToggleFavourite(_currentTrackPath)
    End Sub

    Private Sub TrackFavouriteButton_Click(sender As Object, e As RoutedEventArgs)
        Dim button = TryCast(sender, Button)
        If button IsNot Nothing Then ToggleFavourite(TryCast(button.Tag, String))
        e.Handled = True
    End Sub

    Private Sub ToggleFavourite(trackPath As String)
        Dim entry As TrackEntry = Nothing
        If String.IsNullOrWhiteSpace(trackPath) OrElse Not _trackCache.TryGetValue(trackPath, entry) Then Return
        If Not _favourites.Add(trackPath) Then _favourites.Remove(trackPath)
        entry.IsFavourite = _favourites.Contains(trackPath)
        UpdateFavouriteButton()
        If _currentMode = "Favourites" Then RefreshVisibleTracks()
        ScheduleSaveSettings()
    End Sub

    Private Sub UpdateFavouriteButton()
        If FavouriteButton Is Nothing Then Return
        Dim liked = Not String.IsNullOrWhiteSpace(_currentTrackPath) AndAlso _favourites.Contains(_currentTrackPath)
        FavouriteButton.Content = If(liked, ChrW(&HEB52), ChrW(&HEB51))
        FavouriteButton.Foreground = If(liked, GetThemeBrush("AccentBrush", Color.FromRgb(208, 188, 255)), GetThemeBrush("MutedBrush", Color.FromRgb(202, 196, 208)))
        FavouriteButton.ToolTip = If(liked, "Remove from Favourites", "Save to Favourites")
        System.Windows.Automation.AutomationProperties.SetName(FavouriteButton, If(liked, "Remove current song from Favourites", "Save current song to Favourites"))
        FavouriteButton.IsEnabled = Not String.IsNullOrWhiteSpace(_currentTrackPath)
    End Sub

    Private Sub QueueTrackButton_Click(sender As Object, e As RoutedEventArgs)
        Dim button = TryCast(sender, Button)
        Dim trackPath = If(button Is Nothing, Nothing, TryCast(button.Tag, String))
        If Not String.IsNullOrWhiteSpace(trackPath) Then PlayTrack(trackPath)
    End Sub

    Private Sub MuteButton_Click(sender As Object, e As RoutedEventArgs)
        If VolumeSlider.Value > 0 Then
            _lastAudibleVolume = VolumeSlider.Value
            VolumeSlider.Value = 0
        Else
            VolumeSlider.Value = _lastAudibleVolume
        End If
    End Sub

    Private Sub SearchFocusButton_Click(sender As Object, e As RoutedEventArgs)
        SearchBox.Focus()
        SearchBox.SelectAll()
    End Sub

    Private Sub ClearSearchButton_Click(sender As Object, e As RoutedEventArgs)
        SearchBox.Clear()
        SearchBox.Focus()
    End Sub

    Private Sub EmptyLibraryActionButton_Click(sender As Object, e As RoutedEventArgs)
        If Not String.IsNullOrWhiteSpace(SearchBox.Text) Then
            SearchBox.Clear()
            SearchBox.Focus()
        ElseIf _currentMode <> "Browse" AndAlso _libraryEntries.Count > 0 Then
            ChangeLibraryView("Browse")
        Else
            ChooseFolderButton_Click(sender, e)
        End If
    End Sub

    Private Sub SearchBox_TextChanged(sender As Object, e As TextChangedEventArgs)
        If _isInitialized Then RefreshVisibleTracks()
    End Sub

    Private Sub VolumeSlider_ValueChanged(sender As Object, e As RoutedPropertyChangedEventArgs(Of Double))
        If Not _isInitialized OrElse AudioPlayer Is Nothing OrElse VolumeSlider Is Nothing Then Return
        AudioPlayer.Volume = VolumeSlider.Value
        If VolumeSlider.Value > 0 Then _lastAudibleVolume = VolumeSlider.Value
        UpdateVolumeButton()
        ScheduleSaveSettings()
    End Sub

    Private Sub UpdateVolumeButton()
        If MuteButton Is Nothing Then Return
        Dim muted = VolumeSlider.Value <= 0
        MuteButton.Content = If(muted, ChrW(&HE74F), ChrW(&HE767))
        MuteButton.ToolTip = If(muted, "Unmute", "Mute")
        System.Windows.Automation.AutomationProperties.SetName(MuteButton, If(muted, "Unmute", "Mute"))
    End Sub

    Private Sub UpdatePlaybackControls()
        If PlayPauseButton Is Nothing Then Return
        PlayPauseButton.Content = If(_isPlaying, ChrW(&HE769), ChrW(&HE768))
        PlayPauseButton.ToolTip = If(_isPlaying, "Pause", "Play")
        System.Windows.Automation.AutomationProperties.SetName(PlayPauseButton, If(_isPlaying, "Pause", "Play"))
        PlayPauseButton.Background = GetThemeBrush("AccentBrush", Color.FromRgb(208, 188, 255))
        PlayPauseButton.Foreground = GetThemeBrush("OnPrimaryBrush", Color.FromRgb(56, 30, 114))
        PlayStateText.Text = If(_isPlaying, "Now playing", If(String.IsNullOrWhiteSpace(_currentTrackPath), "Your collection, your way", "Paused"))
        PlayerStatusText.Text = If(_isPlaying, "Playing from your collection", If(_libraryEntries.Count > 0, "Local files · ready when you are", "Choose a folder to get started"))
        Dim accent = GetThemeBrush("AccentBrush", Color.FromRgb(208, 188, 255))
        Dim muted = GetThemeBrush("MutedBrush", Color.FromRgb(202, 196, 208))
        ShuffleButton.Foreground = If(_shuffle, accent, muted)
        ShuffleButton.ToolTip = If(_shuffle, "Shuffle is on", "Shuffle is off")
        System.Windows.Automation.AutomationProperties.SetName(ShuffleButton, If(_shuffle, "Shuffle on", "Shuffle off"))
        RepeatButton.Foreground = If(_repeat > 0, accent, muted)
        RepeatButton.Content = ChrW(&HE8EE)
        RepeatButton.ToolTip = If(_repeat = 0, "Repeat is off · click to repeat queue", If(_repeat = 1, "Repeat queue · click to repeat song", "Repeat song · click to turn off"))
        System.Windows.Automation.AutomationProperties.SetName(RepeatButton, If(_repeat = 0, "Repeat off", If(_repeat = 1, "Repeat queue", "Repeat song")))
        UpdateControlAvailability()
    End Sub

    Private Sub UpdateControlAvailability()
        PlayPauseButton.IsEnabled = AudioPlayer.Source IsNot Nothing OrElse (Not _isLoading AndAlso _displayedTrackEntries.Count > 0)
        ShuffleButton.IsEnabled = _playbackOrder.Count > 0
        RepeatButton.IsEnabled = _playbackOrder.Count > 0
        PreviousTrackButton.IsEnabled = _playbackOrder.Count > 0
        NextTrackButton.IsEnabled = _playbackOrder.Count > 0
        CollectionPlayButton.IsEnabled = Not _isLoading AndAlso _displayedTrackEntries.Count > 0
    End Sub

    Private Function GetThemeBrush(resourceKey As String, fallback As Color) As Brush
        Return If(TryCast(TryFindResource(resourceKey), Brush), New SolidColorBrush(fallback))
    End Function

    Private Shared Function IsSupportedAudioFile(filePath As String) As Boolean
        Return {".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma"}.Contains(IO.Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase)
    End Function

    Private Shared Function FormatTime(value As TimeSpan) As String
        Return If(value.TotalHours >= 1, value.ToString("h\:mm\:ss"), value.ToString("m\:ss"))
    End Function

    Private Shared Function FormatLibraryDuration(value As TimeSpan) As String
        If value.TotalHours >= 1 Then Return $"{Math.Floor(value.TotalHours):N0} hr {value.Minutes} min"
        Return $"{Math.Max(1, CInt(Math.Floor(value.TotalMinutes)))} min"
    End Function

    Private Function ReadSettings() As PlayerSettings
        Try
            If File.Exists(_settingsPath) Then Return If(JsonSerializer.Deserialize(Of PlayerSettings)(File.ReadAllText(_settingsPath)), New PlayerSettings())
        Catch ex As Exception
            ' A missing or damaged preferences file should never prevent startup.
        End Try
        Return New PlayerSettings()
    End Function

    Private Sub ScheduleSaveSettings()
        If Not _isInitialized OrElse _settingsTimer Is Nothing Then Return
        _settingsTimer.Stop()
        _settingsTimer.Start()
    End Sub

    Private Sub SettingsTimer_Tick(sender As Object, e As EventArgs)
        _settingsTimer.Stop()
        SaveSettings()
    End Sub

    Private Sub SaveSettings()
        _settings.MusicFolder = _currentFolder
        _settings.Volume = VolumeSlider.Value
        _settings.Favourites = _favourites.OrderBy(Function(path) path, StringComparer.OrdinalIgnoreCase).ToList()
        _settings.Shuffle = _shuffle
        _settings.Repeat = _repeat
        _settings.LibraryView = _currentMode
        Try
            Directory.CreateDirectory(IO.Path.GetDirectoryName(_settingsPath))
            Dim temporaryPath = _settingsPath & ".tmp"
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_settings, New JsonSerializerOptions With {.WriteIndented = True}))
            File.Move(temporaryPath, _settingsPath, True)
        Catch ex As Exception
            PlayerStatusText.Text = "Your music is ready. Preferences couldn’t be saved on this device."
        End Try
    End Sub

    Private Sub Window_Closed(sender As Object, e As EventArgs)
        _isClosed = True
        If _scanCancellation IsNot Nothing Then _scanCancellation.Cancel()
        If _positionTimer IsNot Nothing Then _positionTimer.Stop()
        If _settingsTimer IsNot Nothing Then _settingsTimer.Stop()
        AudioPlayer.Stop()
        If _isInitialized Then SaveSettings()
    End Sub
End Class

