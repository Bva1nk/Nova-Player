Imports System.Windows.Controls
Imports System.Windows.Input

Partial Class MainWindow
    Private _queueVisibilityOverride As Boolean? = Nothing

    Private Sub Artwork_SizeChanged(sender As Object, e As SizeChangedEventArgs)
        Dim artwork = TryCast(sender, Border)
        If artwork Is Nothing OrElse artwork.ActualWidth <= 0 OrElse artwork.ActualHeight <= 0 Then Return
        Dim radius = artwork.CornerRadius.TopLeft
        artwork.Clip = New RectangleGeometry(New Rect(0, 0, artwork.ActualWidth, artwork.ActualHeight), radius, radius)
    End Sub

    Private Sub Window_SizeChanged(sender As Object, e As SizeChangedEventArgs)
        UpdateResponsiveLayout()
    End Sub

    Private Sub UpdateResponsiveLayout()
        If NowPlayingPanel Is Nothing OrElse ListeningColumn Is Nothing Then Return
        Dim layoutWidth = If(ActualWidth > 0, ActualWidth, Width)
        Dim layoutHeight = If(ActualHeight > 0, ActualHeight, Height)
        Dim compactHeight = layoutHeight < 800
        ListeningColumn.Width = New GridLength(If(layoutWidth < 1200, 316, 360))
        CollectionHeaderRow.Height = New GridLength(If(compactHeight, 94, 112))
        CollectionToolbarRow.Height = New GridLength(If(compactHeight, 54, 62))
        HeroArtwork.Height = If(compactHeight, 120, 242)
        PlayerContentStack.Margin = If(compactHeight, New Thickness(18, 16, 18, 16), New Thickness(22, 20, 22, 22))
        PlayerHeadingRow.Margin = New Thickness(2, 0, 0, If(compactHeight, 10, 16))
        TrackMetadataPanel.Margin = New Thickness(2, If(compactHeight, 12, 18), 0, 0)
        TransportControls.Margin = If(compactHeight, New Thickness(0, 8, 0, 6), New Thickness(0, 14, 0, 10))
        CurrentAlbumText.Visibility = If(compactHeight, Visibility.Collapsed, Visibility.Visible)
        PlayerStatusText.Visibility = If(compactHeight, Visibility.Collapsed, Visibility.Visible)
        EmptyLibraryIcon.Height = If(compactHeight, 46, 64)
        EmptyLibraryIcon.Width = EmptyLibraryIcon.Height
        EmptyLibraryIcon.Margin = New Thickness(0, 0, 0, If(compactHeight, 10, 18))
        EmptyLibraryTitle.FontSize = If(compactHeight, 20, 23)
        Dim showQueue = If(_queueVisibilityOverride, Not compactHeight)
        NextTrackQueue.Visibility = If(showQueue, Visibility.Visible, Visibility.Collapsed)
        QueueHeader.Visibility = NextTrackQueue.Visibility
        QueueToggleButton.Content = ChrW(&HE90B)
        QueueToggleButton.Background = If(showQueue, CType(FindResource("SecondaryContainerBrush"), Brush), Brushes.Transparent)
        QueueToggleButton.ToolTip = If(showQueue, "Hide upcoming songs", "Show upcoming songs")
        QueueToggleButton.Foreground = CType(FindResource("AccentBrush"), Brush)
    End Sub

    Private Sub QueueToggleButton_Click(sender As Object, e As RoutedEventArgs)
        _queueVisibilityOverride = NextTrackQueue.Visibility <> Visibility.Visible
        UpdateResponsiveLayout()
    End Sub
    Private Sub MinimizeWindow_Click(sender As Object, e As RoutedEventArgs)
        WindowState = WindowState.Minimized
    End Sub

    Private Sub MaximizeWindow_Click(sender As Object, e As RoutedEventArgs)
        WindowState = If(WindowState = WindowState.Maximized, WindowState.Normal, WindowState.Maximized)
        MaximizeWindowButton.Content = If(WindowState = WindowState.Maximized, ChrW(&HE923), ChrW(&HE922))
    End Sub

    Private Sub CloseWindow_Click(sender As Object, e As RoutedEventArgs)
        Close()
    End Sub

    Private Sub Window_PreviewKeyDown(sender As Object, e As KeyEventArgs)
        If e.Key = Key.F AndAlso Keyboard.Modifiers = ModifierKeys.Control Then
            SearchBox.Focus()
            SearchBox.SelectAll()
            e.Handled = True
        ElseIf e.Key = Key.Escape AndAlso SearchBox.IsKeyboardFocusWithin Then
            SearchBox.Clear()
            SongsListBox.Focus()
            e.Handled = True
        ElseIf e.Key = Key.Space AndAlso Not TypeOf Keyboard.FocusedElement Is TextBox AndAlso Not TypeOf Keyboard.FocusedElement Is Button Then
            PlayPauseButton_Click(PlayPauseButton, New RoutedEventArgs())
            e.Handled = True
        End If
    End Sub
End Class

