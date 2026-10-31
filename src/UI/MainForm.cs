using System.Diagnostics;
using LiteDrops.Mining;
using LiteDrops.Twitch;
using Microsoft.Win32;

namespace LiteDrops.UI;

sealed class MainForm : Form
{
    readonly AppSettings _settings;
    readonly TwitchApi _api;
    readonly Icon _icon;
    bool _startHidden;

    // header
    readonly Panel _header = new() { Dock = DockStyle.Top, Height = 66, BackColor = Theme.Background };
    readonly StatusPill _status = new() { Size = new Size(150, 34) };
    readonly GearButton _gear = new();
    // now watching
    readonly CardPanel _watchCard = new() { Dock = DockStyle.Top, Height = 204 };
    readonly Panel _watchView = new() { BackColor = Theme.Card };
    readonly Label _channel = MakeLabel(Theme.Big, Theme.Text);
    readonly Label _game = MakeLabel(Theme.Body, Theme.SubText);
    readonly Label _drop = MakeLabel(Theme.BodyBold, Theme.Text);
    readonly SlimProgress _progress = new() { Height = 10 };
    readonly Label _minutes = MakeLabel(Theme.Body, Theme.Text);
    readonly Label _percent = MakeLabel(Theme.BodyBold, Theme.Accent, ContentAlignment.MiddleRight);
    readonly Label _campaign = MakeLabel(Theme.Small, Theme.SubText);
    string? _watchingLogin;
    // login
    readonly Panel _loginView = new() { BackColor = Theme.Card, Visible = false };
    readonly Label _loginText = MakeLabel(Theme.Body, Theme.SubText);
    readonly SearchBox _loginUrl = new() { Height = 38, Visible = false };
    readonly AccentButton _loginButton = new() { Size = new Size(190, 36) };
    // games
    readonly CardPanel _gamesCard = new() { Dock = DockStyle.Fill };
    readonly GameList _gameList = new();
    readonly SearchBox _search = new() { Height = 38 };
    readonly AccentButton _addButton = new() { Size = new Size(80, 38) };
    readonly SuggestionList _suggestions = new() { Visible = false };
    readonly UndoBar _undo = new();
    readonly System.Windows.Forms.Timer _undoTimer = new() { Interval = 7000 };
    readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 300 };
    readonly System.Windows.Forms.Timer _wakeTimer = new() { Interval = 1500 };
    CancellationTokenSource? _searchCts;
    (GameEntry Game, int Index)? _lastRemoved;
    // campaigns
    readonly CardPanel _campaignCard = new() { Dock = DockStyle.Bottom, Height = 180 };
    readonly CampaignListBox _campaignList = new();
    readonly Label _campaignEmpty = MakeLabel(Theme.Small, Theme.SubText, ContentAlignment.MiddleCenter);
    // footer
    readonly Label _user = MakeLabel(Theme.Body, Theme.SubText);
    readonly LinkLabel _logout = new()
    {
        AutoSize = true, Font = Theme.BodyBold, LinkColor = Theme.Accent,
        ActiveLinkColor = Theme.AccentDim, LinkBehavior = LinkBehavior.HoverUnderline, BackColor = Theme.Background,
    };
    readonly Label _ram = MakeLabel(Theme.Body, Theme.SubText, ContentAlignment.MiddleRight);

    readonly NotifyIcon _tray;
    readonly ClaimHistory _history = ClaimHistory.Load();
    readonly TextLink _historyLink = new() { BackColor = Theme.Card, Anchor = AnchorStyles.Top | AnchorStyles.Right };
    readonly PillLink _updatePill = new() { Visible = false, Font = Theme.Small, BackColor = Theme.Background };
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 15_000 };
    string? _updateUrl;
    readonly ToolTip _tips = new();
    readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 3000 };

    Miner? _miner;
    CancellationTokenSource? _minerCts;
    bool _loggingIn;
    bool _exiting;
    bool _started;
    DateTime _lastTrim;

    public MainForm(AppSettings settings, bool startHidden)
    {
        _settings = settings;
        _startHidden = startHidden;
        _api = new TwitchApi(settings.DeviceId) { AccessToken = settings.AccessToken };
        _icon = LoadIcon();

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "LiteDrops";
        Icon = _icon;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        ClientSize = new Size(480, 820);
        MinimumSize = new Size(440, 680);
        Padding = new Padding(14, 0, 14, 0);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildHeader();
        BuildWatchCard();
        BuildGamesCard();
        BuildCampaignCard();
        var footer = BuildFooter();

        // docking order: last added is laid out first
        Controls.Add(_gamesCard);
        Controls.Add(Spacer(DockStyle.Top));
        Controls.Add(_watchCard);
        Controls.Add(_header);
        Controls.Add(Spacer(DockStyle.Bottom));
        Controls.Add(_campaignCard);
        Controls.Add(Spacer(DockStyle.Bottom));
        Controls.Add(footer);
        Controls.Add(_suggestions); // floats above everything else
        _suggestions.BringToFront();
        ResumeLayout(false);
        RestoreWindowBounds();

        _tray = new NotifyIcon { Icon = _icon, Text = "LiteDrops", Visible = true, ContextMenuStrip = BuildTrayMenu() };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();

        _statsTimer.Tick += (_, _) => UpdateStats();
        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
        _updateTimer.Start();
        _statsTimer.Start();
        SetStatus(L.Starting, Theme.Accent);
    }

    // ------------------------------------------------------------ layout

    void BuildHeader()
    {
        var logo = new PictureBox
        {
            Image = new Icon(_icon, 64, 64).ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = new Rectangle(0, 15, 36, 36),
        };
        var title = new Label
        {
            Text = "LiteDrops", Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true,
            Location = new Point(42, 14), BackColor = Theme.Background,
        };
        _status.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _gear.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _gear.Click += (_, _) => OpenSettings();
        _tips.SetToolTip(_gear, L.Settings);
        _header.Controls.AddRange([logo, title, _gear, _status]);
        _header.Resize += (_, _) =>
        {
            _status.Location = new Point(_header.ClientSize.Width - _status.Width, 16);
            _gear.Location = new Point(_status.Left - _gear.Width - 8, 16);
        };
    }

    void BuildWatchCard()
    {
        _watchCard.Heading = L.NowWatching;
        _watchCard.Padding = new Padding(16, 38, 16, 12);
        _watchView.Dock = DockStyle.Fill;
        _loginView.Dock = DockStyle.Fill;
        _channel.SetBounds(0, 0, 400, 30);
        _game.SetBounds(0, 32, 400, 22);
        _drop.SetBounds(0, 56, 400, 22);
        _progress.SetBounds(0, 88, 360, 10);
        _percent.SetBounds(360, 80, 50, 24);
        _minutes.SetBounds(0, 104, 200, 22);
        _campaign.SetBounds(0, 126, 400, 20);
        foreach (var c in new Control[] { _channel, _game, _drop, _campaign, _progress })
            c.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _percent.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _channel.AutoSize = true;
        _channel.MaximumSize = new Size(400, 30);
        _channel.Click += (_, _) =>
        {
            if (_watchingLogin is not null) OpenUrl($"https://www.twitch.tv/{_watchingLogin}");
        };
        _watchView.Controls.AddRange([_channel, _game, _drop, _progress, _percent, _minutes, _campaign]);
        _watchView.Resize += (_, _) =>
        {
            var w = _watchView.ClientSize.Width;
            _percent.Left = w - _percent.Width;
            _progress.Width = Math.Max(10, _percent.Left - 8);
            _channel.MaximumSize = new Size(w, _channel.Height);
        };

        _loginText.SetBounds(0, 0, 410, 62);
        _loginText.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _loginText.Text = L.LoginPrompt;
        _loginUrl.SetBounds(0, 68, 410, 38);
        _loginUrl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _loginUrl.Box.PlaceholderText = L.PastePlaceholder;
        _loginUrl.Box.TextChanged += async (_, _) => await TokenPastedAsync();
        _loginButton.Text = L.LoginButton;
        _loginButton.Location = new Point(0, 68);
        _loginButton.Click += (_, _) => StartLogin();
        _loginView.Controls.AddRange([_loginText, _loginUrl, _loginButton]);

        _watchCard.Controls.AddRange([_watchView, _loginView]);
    }

    void BuildGamesCard()
    {
        _gamesCard.Heading = L.Games;
        _gamesCard.Hint = L.GamesHint;
        _gamesCard.Padding = new Padding(16, 40, 16, 14);
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card };

        _gameList.Toggled += _ => GamesChanged();
        _gameList.Moved += MoveGame;
        _gameList.RemoveRequested += RemoveGame;

        _search.Box.PlaceholderText = L.SearchPlaceholder;
        _search.Box.KeyDown += SearchKeyDown;
        _search.Box.TextChanged += (_, _) =>
        {
            _searchTimer.Stop();
            if (_search.Box.Text.Trim().Length >= 2) _searchTimer.Start();
            else HideSuggestions();
        };
        _search.Box.LostFocus += (_, _) => BeginInvoke(() =>
        {
            if (!_suggestions.Focused && !_search.Box.Focused) HideSuggestions();
        });
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await SearchGamesAsync(_search.Box.Text.Trim());
        };
        _suggestions.IsAdded = name => _settings.Games.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _suggestions.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var index = _suggestions.IndexFromPoint(e.Location);
            if (index >= 0) AddGame((string)_suggestions.Items[index]);
        };
        _addButton.Text = L.Add;
        _addButton.Click += (_, _) => AddGame(null);

        _undo.Click += (_, _) => UndoRemove();
        _undoTimer.Tick += (_, _) => HideUndo();
        _wakeTimer.Tick += (_, _) =>
        {
            _wakeTimer.Stop();
            _miner?.Wake();
        };

        body.Controls.AddRange([_gameList, _search, _addButton]);
        body.Resize += (_, _) =>
        {
            var w = body.ClientSize.Width;
            var h = body.ClientSize.Height;
            _addButton.SetBounds(w - _addButton.Width, h - _addButton.Height, _addButton.Width, _addButton.Height);
            _search.SetBounds(0, h - _search.Height, w - _addButton.Width - 10, _search.Height);
            _gameList.SetBounds(0, 0, w, Math.Max(0, _search.Top - 12));
            PlaceSuggestions();
        };
        _gamesCard.Controls.AddRange([body, _undo]);
        _gamesCard.Resize += (_, _) => PlaceUndo();
        _gameList.Items = _settings.Games;
    }

    void BuildCampaignCard()
    {
        _campaignCard.Heading = L.Campaigns;
        _campaignCard.Padding = new Padding(16, 38, 12, 10);
        _campaignList.Dock = DockStyle.Fill;
        _campaignEmpty.Dock = DockStyle.Fill;
        _campaignEmpty.Text = L.NoCampaigns;
        _campaignList.LinkClicked += url => OpenUrl(url ?? "https://www.twitch.tv/drops/campaigns");
        _historyLink.Text = L.History;
        _historyLink.Click += (_, _) => OpenHistory();
        _campaignCard.Resize += (_, _) =>
            _historyLink.Location = new Point(_campaignCard.Width - _historyLink.Width - 16, 13);
        _campaignCard.Controls.AddRange([_campaignList, _campaignEmpty, _historyLink]);
        UpdateCampaignEmpty();
    }

    Panel BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Theme.Background };
        _user.SetBounds(0, 10, 220, 24);
        _user.AutoSize = true;
        _logout.Text = L.LogOut;
        _logout.Location = new Point(230, 13);
        _logout.LinkClicked += (_, _) => Logout();
        _ram.SetBounds(360, 10, 90, 24);
        _ram.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _updatePill.Click += (_, _) => { if (_updateUrl is not null) OpenUrl(_updateUrl); };
        footer.Controls.AddRange([_logout, _user, _ram, _updatePill]);
        footer.Resize += (_, _) =>
        {
            _ram.Left = footer.ClientSize.Width - _ram.Width;
            _updatePill.Location = new Point(_ram.Left - _updatePill.Width - 6, (footer.ClientSize.Height - _updatePill.Height) / 2);
        };
        return footer;
    }

    ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(L.Show, null, (_, _) => ShowFromTray());
        menu.Items.Add(L.SettingsMenu, null, (_, _) =>
        {
            ShowFromTray();
            OpenSettings();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.Exit, null, (_, _) => ExitApp());
        return menu;
    }

    static Label MakeLabel(Font font, Color color, ContentAlignment align = ContentAlignment.MiddleLeft) => new()
    {
        Font = font, ForeColor = color, BackColor = Color.Transparent, TextAlign = align,
        AutoSize = false, AutoEllipsis = true, UseMnemonic = false,
    };

    static Panel Spacer(DockStyle dock) => new() { Dock = dock, Height = 12, BackColor = Theme.Background };

    static Icon LoadIcon()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }

    static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    // ------------------------------------------------------------ lifecycle

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && value)
        {
            // started with Windows: stay in the tray, but still run the miner
            _startHidden = false;
            CreateHandle();
            BeginInvoke(async () => await StartupAsync());
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(Handle);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await StartupAsync();
    }

    async Task StartupAsync()
    {
        if (_started) return;
        _started = true;
        ShowWatching(null);
        TwitchUser? user = null;
        try
        {
            user = await _api.ValidateAsync(CancellationToken.None);
        }
        catch (HttpRequestException ex)
        {
            SetStatus(L.Offline, Theme.Red);
            _loginText.Text = L.CantReach(ex.Message);
        }
        if (user is null)
        {
            var hadToken = !string.IsNullOrEmpty(_settings.AccessToken);
            ShowLogin();
            if (hadToken) _loginText.Text = L.LoginExpired; // ex. a token of the old login method
        }
        else StartMiner(user);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && !_exiting) HideToTray();
        PlaceSuggestions();
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        HideSuggestions();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveBounds();
        if (!_exiting && _settings.CloseToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        _exiting = true;
        _minerCts?.Cancel();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    void RestoreWindowBounds()
    {
        if (_settings.WindowBounds?.Split(',') is not [var x, var y, var w, var h]) return;
        if (!int.TryParse(x, out var left) || !int.TryParse(y, out var top)
            || !int.TryParse(w, out var width) || !int.TryParse(h, out var height)) return;
        var bounds = new Rectangle(left, top, width, height);
        // only if the title bar would be on a connected screen
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds with { Height = 40 }))) return;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
    }

    void SaveBounds()
    {
        if (WindowState != FormWindowState.Normal || !Visible) return;
        var b = Bounds;
        var value = $"{b.X},{b.Y},{b.Width},{b.Height}";
        if (value == _settings.WindowBounds) return;
        _settings.WindowBounds = value;
        _settings.Save();
    }

    void HideToTray()
    {
        SaveBounds();
        HideSuggestions();
        Hide();
        Memory.Trim();
        _lastTrim = DateTime.UtcNow;
    }

    void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    void ExitApp()
    {
        _exiting = true;
        Close();
    }

    void OpenSettings()
    {
        if (OwnedForms.OfType<SettingsDialog>().FirstOrDefault() is { } open)
        {
            open.Activate();
            return;
        }
        // not modal: the main window stays usable while the settings are open
        Log.Info("Settings opened");
        var dialog = new SettingsDialog(_settings);
        dialog.MiningSettingsChanged += () => _miner?.Wake();
        dialog.FormClosed += (_, e) =>
        {
            Log.Info($"Settings closed ({e.CloseReason})");
            ChangeLanguage(dialog.Language);
            dialog.Dispose();
        };
        dialog.ShowOver(this);
    }

    void OpenHistory()
    {
        if (OwnedForms.OfType<HistoryDialog>().FirstOrDefault() is { } open)
        {
            open.Activate();
            return;
        }
        var dialog = new HistoryDialog(_history);
        dialog.FormClosed += (_, _) => dialog.Dispose();
        dialog.ShowOver(this);
    }

    async Task CheckForUpdateAsync()
    {
        _updateTimer.Interval = (int)TimeSpan.FromHours(12).TotalMilliseconds; // first check 15 s after start
        if (await UpdateChecker.CheckAsync(CancellationToken.None) is not { } update) return;
        Log.Info($"Update available: v{update.Version}");
        _updateUrl = update.Url;
        _updatePill.Text = L.UpdateAvailable(update.Version);
        _updatePill.Location = new Point(_ram.Left - _updatePill.Width - 6, _updatePill.Parent is { } p
            ? (p.ClientSize.Height - _updatePill.Height) / 2 : 8);
        _updatePill.Visible = true;
    }

    void ChangeLanguage(string code)
    {
        if (code == _settings.Language) return;
        _settings.Language = code;
        _settings.Save();
        // restart so every text and font is rebuilt in the new language
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = false });
        ExitApp();
    }

    // ------------------------------------------------------------ login

    void ShowLogin()
    {
        _watchView.Visible = false;
        _loginView.Visible = true;
        _loginUrl.Visible = false;
        _loginUrl.Box.Clear();
        _loginButton.Text = L.LoginButton;
        _loginButton.Location = new Point(0, 68);
        _loginButton.Enabled = true;
        _user.Text = L.NotLoggedIn;
        _logout.Visible = false;
        SetStatus(L.LoggedOut, Theme.SubText);
    }

    /// <summary>Opens Twitch's login in the browser; the user pastes the address it ends up on.</summary>
    void StartLogin()
    {
        OpenUrl(TwitchApi.AuthorizeUrl);
        _loginText.Text = L.PasteUrl;
        _loginUrl.Visible = true;
        _loginUrl.Box.Focus();
        _loginButton.Text = L.OpenAgain;
        _loginButton.Location = new Point(0, 114);
    }

    async Task TokenPastedAsync()
    {
        var text = _loginUrl.Box.Text.Trim();
        if (text.Length == 0 || _loggingIn) return;
        var token = TwitchApi.ParseToken(text);
        if (token is null)
        {
            // typing or pasting half an address: only complain once it looks like a whole one
            if (text.Length > 40) _loginText.Text = L.NoTokenInUrl;
            return;
        }
        _loggingIn = true;
        _loginUrl.Enabled = false;
        try
        {
            _api.AccessToken = token;
            var user = await _api.ValidateAsync(CancellationToken.None) ?? throw new TwitchApiException("Login was not accepted");
            _settings.AccessToken = token;
            _settings.Save();
            _loginUrl.Box.Clear(); // the address holds the token
            Log.Info("Logged in");
            StartMiner(user);
        }
        catch (Exception ex) when (ex is TwitchApiException or HttpRequestException)
        {
            Log.Error($"Login failed: {ex.Message}");
            _api.AccessToken = null;
            ShowLogin();
            _loginText.Text = L.LoginFailed(ex.Message);
        }
        finally
        {
            _loggingIn = false;
            _loginUrl.Enabled = true;
        }
    }

    void Logout()
    {
        _minerCts?.Cancel();
        _miner = null;
        _api.AccessToken = null;
        _settings.AccessToken = null;
        _settings.Save();
        Log.Info("Logged out");
        _loginText.Text = L.LoginPrompt;
        _campaignList.Items.Clear();
        UpdateCampaignEmpty();
        ShowLogin();
    }

    // ------------------------------------------------------------ mining

    void StartMiner(TwitchUser user)
    {
        _loginView.Visible = false;
        _watchView.Visible = true;
        _user.Text = L.LoggedInAs(user.Login);
        _logout.Visible = true;
        _logout.Left = Math.Min(_user.Left + TextRenderer.MeasureText(_user.Text, _user.Font).Width + 12, _ram.Left - _logout.Width);

        _minerCts?.Cancel();
        _minerCts = new CancellationTokenSource();
        var ct = _minerCts.Token;
        var miner = new Miner(_api, _settings, user);
        _miner = miner;
        miner.Changed += snapshot =>
        {
            if (IsHandleCreated && !_exiting) BeginInvoke(() => { if (_miner == miner) Render(snapshot); });
        };
        miner.Claimed += (game, campaign, drop) =>
        {
            if (!IsHandleCreated || _exiting) return;
            BeginInvoke(() =>
            {
                _history.Add(game, campaign, drop);
                OwnedForms.OfType<HistoryDialog>().FirstOrDefault()?.Refresh(_history);
                _tray.ShowBalloonTip(5000, L.DropClaimed, $"{game} — {drop}", ToolTipIcon.Info);
            });
        };
        _ = Task.Run(async () =>
        {
            try
            {
                await miner.RunAsync(ct);
            }
            catch (AuthExpiredException)
            {
                BeginInvoke(() =>
                {
                    _settings.AccessToken = null;
                    _settings.Save();
                    ShowLogin();
                    _loginText.Text = L.LoginExpired;
                });
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Error($"Miner stopped: {ex}");
                BeginInvoke(() => SetStatus(L.Stopped, Theme.Red));
            }
        }, ct);
    }

    void Render(MinerSnapshot snapshot)
    {
        var (text, color) = snapshot.Status switch
        {
            MinerStatus.Mining => (L.Mining, Theme.Green),
            MinerStatus.Idle => (L.Idle, Theme.Amber),
            MinerStatus.Starting => (L.Searching, Theme.Accent),
            MinerStatus.Error => (L.Error, Theme.Red),
            _ => (L.LoggedOut, Theme.SubText),
        };
        SetStatus(text, color);
        if (snapshot.Status == MinerStatus.LoginRequired) return;
        ShowWatching(snapshot.Watching, snapshot.StatusText);

        _campaignList.BeginUpdate();
        _campaignList.Items.Clear();
        foreach (var row in snapshot.Campaigns) _campaignList.Items.Add(row);
        _campaignList.EndUpdate();
        UpdateCampaignEmpty();

        var tip = snapshot.Watching is { } w
            ? $"LiteDrops — {w.GameName} {L.Minutes(w.CurrentMinutes, w.RequiredMinutes)}"
            : $"LiteDrops — {snapshot.StatusText}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    void ShowWatching(WatchInfo? w, string? idleText = null)
    {
        _watchingLogin = w?.Channel.Login;
        _channel.Cursor = w is null ? Cursors.Default : Cursors.Hand;
        _tips.SetToolTip(_channel, w is null ? "" : L.OpenChannel);
        if (w is null)
        {
            _channel.Text = L.NotWatching;
            _game.Text = idleText ?? "";
            _drop.Text = "";
            _progress.Value = 0;
            _minutes.Text = "";
            _percent.Text = "";
            _campaign.Text = "";
            return;
        }
        var ratio = w.RequiredMinutes == 0 ? 0 : (double)w.CurrentMinutes / w.RequiredMinutes;
        _channel.Text = w.Channel.DisplayName;
        _game.Text = w.GameName;
        _drop.Text = w.DropName;
        _progress.Value = ratio;
        _minutes.Text = L.Minutes(w.CurrentMinutes, w.RequiredMinutes);
        _percent.Text = $"{ratio:0%}";
        _campaign.Text = L.CampaignLine(w.CampaignName, w.CampaignEndAt);
    }

    void UpdateCampaignEmpty()
    {
        _campaignEmpty.Visible = _campaignList.Items.Count == 0;
        _campaignList.Visible = !_campaignEmpty.Visible;
    }

    void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.DotColor = color;
    }

    void UpdateStats()
    {
        _ram.Text = $"RAM {Memory.PrivateWorkingSetMB():0} MB";
        if (!Visible && DateTime.UtcNow - _lastTrim > TimeSpan.FromMinutes(5))
        {
            Memory.Trim();
            _lastTrim = DateTime.UtcNow;
        }
    }

    // ------------------------------------------------------------ games

    void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter:
                e.SuppressKeyPress = true;
                AddGame(null);
                break;
            case Keys.Down when _suggestions.Visible:
                e.Handled = true;
                _suggestions.SelectedIndex = Math.Min(_suggestions.Items.Count - 1, _suggestions.SelectedIndex + 1);
                break;
            case Keys.Up when _suggestions.Visible:
                e.Handled = true;
                _suggestions.SelectedIndex = Math.Max(-1, _suggestions.SelectedIndex - 1);
                break;
            case Keys.Escape:
                e.SuppressKeyPress = true;
                if (_suggestions.Visible) HideSuggestions();
                else _search.Box.Clear();
                break;
        }
    }

    /// <summary>Adds the given name, or else the highlighted / exactly matching / first suggestion.</summary>
    void AddGame(string? name)
    {
        var typed = _search.Box.Text.Trim();
        var suggestions = _suggestions.Items.Cast<string>().ToList();
        name ??= _suggestions.Visible && _suggestions.SelectedItem is string picked ? picked
            : suggestions.FirstOrDefault(n => n.Equals(typed, StringComparison.OrdinalIgnoreCase))
              ?? (_suggestions.Visible ? suggestions.FirstOrDefault() : null)
              ?? typed;
        if (name.Length == 0) return;
        _search.Box.Clear();
        HideSuggestions();
        var game = _settings.Games.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (game is not null)
        {
            game.Enabled = true;
        }
        else
        {
            game = new GameEntry { Name = name };
            // copy-on-write: the miner may be enumerating the current list on another thread
            _settings.Games = [.. _settings.Games, game];
        }
        _gameList.Items = _settings.Games;
        _gameList.Reveal(game);
        GamesChanged();
    }

    async Task SearchGamesAsync(string query)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        try
        {
            var names = await _api.SearchGamesAsync(query, ct);
            Log.Info($"Game search \"{query}\": {names.Count} result(s)");
            if (ct.IsCancellationRequested || _search.Box.Text.Trim() != query) return;
            _suggestions.BeginUpdate();
            _suggestions.Items.Clear();
            foreach (var n in names) _suggestions.Items.Add(n);
            _suggestions.EndUpdate();
            _suggestions.Visible = names.Count > 0;
            PlaceSuggestions();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is TwitchApiException or HttpRequestException or AuthExpiredException)
        {
            Log.Warn($"Game search failed: {ex.Message}");
        }
    }

    /// <summary>Puts the dropdown right under the search box, or above it if there's no room.</summary>
    void PlaceSuggestions()
    {
        if (!_suggestions.Visible || !_search.IsHandleCreated) return;
        var s = DeviceDpi / 96f;
        var origin = PointToClient(_search.Parent!.PointToScreen(_search.Location));
        var height = Math.Min(_suggestions.Items.Count, 6) * _suggestions.ItemHeight + 2;
        var below = origin.Y + _search.Height + (int)(4 * s);
        var top = below + height <= ClientSize.Height ? below : origin.Y - height - (int)(4 * s);
        _suggestions.SetBounds(origin.X, top, _search.Width, height);
        _suggestions.BringToFront();
    }

    void HideSuggestions()
    {
        _searchCts?.Cancel();
        _suggestions.Visible = false;
        _suggestions.Items.Clear();
    }

    void RemoveGame(GameEntry game)
    {
        var index = _settings.Games.IndexOf(game);
        if (index < 0) return;
        _settings.Games = _settings.Games.Where(g => g != game).ToList();
        _gameList.Items = _settings.Games;
        _lastRemoved = (game, index);
        _undo.Message = L.Removed(game.Name);
        _undo.Visible = true;
        _gamesCard.Hint = "";
        PlaceUndo();
        _undoTimer.Stop();
        _undoTimer.Start();
        GamesChanged();
    }

    void UndoRemove()
    {
        if (_lastRemoved is not var (game, index)) return;
        var list = _settings.Games.ToList();
        list.Insert(Math.Min(index, list.Count), game);
        _settings.Games = list;
        _gameList.Items = _settings.Games;
        _gameList.Reveal(game);
        HideUndo();
        GamesChanged();
    }

    void HideUndo()
    {
        _undoTimer.Stop();
        _lastRemoved = null;
        _undo.Visible = false;
        _gamesCard.Hint = L.GamesHint;
    }

    void PlaceUndo()
    {
        var s = DeviceDpi / 96f;
        _undo.Location = new Point(_gamesCard.Width - _undo.Width - (int)(16 * s), (int)(13 * s));
        _undo.BringToFront();
    }

    void MoveGame(int from, int to)
    {
        var list = _settings.Games.ToList();
        var game = list[from];
        list.RemoveAt(from);
        list.Insert(to, game);
        _settings.Games = list;
        _gameList.Items = _settings.Games;
        GamesChanged();
    }

    void GamesChanged()
    {
        _settings.Save();
        // several quick edits (dragging, toggling) only trigger one new campaign search
        _wakeTimer.Stop();
        _wakeTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>"Start with Windows" via the per-user Run key.</summary>
static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "LiteDrops";
    static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) as string == Command;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(Name, Command);
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
}
