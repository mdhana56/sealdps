using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace DpsMeterUI;

public partial class MainWindow : Window
{
    readonly CaptureEngine _engine = new();
    readonly ObservableCollection<PlayerStat> _players = new();
    readonly Dictionary<uint, PlayerStat> _byToken = new();
    int _nextColor = 0;
    readonly ObservableCollection<TargetStat> _targets = new();
    readonly Dictionary<uint, TargetStat> _targetById = new();
    // Target yang lagi "dibuka" dari tab TARGET: tabel pemain & breakdown skill
    // cuma menghitung damage ke target ini. Null = semua target (tab DPS biasa).
    TargetStat? _scope;

    // Tombol "di luar party": false = cuma diri sendiri + anggota party (default).
    bool _showOutsideParty;

    // Dicek per NAMA (bukan entity ID): ID karakter bisa ganti waktu masuk dungeon /
    // relog, sedangkan 1 karakter = 1 baris.
    bool IsShownStat(PlayerStat stat) =>
        _showOutsideParty || stat == SelfStat || _engine.IsPartyName(stat.Name);

    PlayerStat? SelfStat => _byToken.TryGetValue(CaptureEngine.SELF_TOKEN, out var s) ? s : null;

    PlayerStat? FindByName(string name) => _players.FirstOrDefault(p => p.Name == name);

    /// Gabung baris-baris dengan nama sama jadi 1 (baris diri sendiri selalu yang
    /// dipertahankan). Terjadi kalau entity ID karakter ganti, atau nama sebuah
    /// token baru ketahuan belakangan (Player N / Kamu -> nama asli).
    void MergeDuplicateNames()
    {
        var self = SelfStat;
        foreach (var group in _players.GroupBy(p => p.Name).Where(g => g.Count() > 1).ToList())
        {
            var keep = group.FirstOrDefault(p => p == self) ?? group.OrderByDescending(p => p.Hits).First();
            foreach (var dup in group.Where(p => p != keep).ToList())
            {
                keep.MergeFrom(dup);
                foreach (var t in _targets) t.MergePlayer(dup, keep);
                foreach (var token in _byToken.Where(kv => kv.Value == dup).Select(kv => kv.Key).ToList())
                    _byToken[token] = keep;
                _players.Remove(dup);
                if (BreakdownView.DataContext == dup) BreakdownView.DataContext = keep;
                if (EquipView.DataContext == dup) EquipView.DataContext = keep;
            }
        }
    }

    // Sesi (encounter): mulai di hit pertama, dianggap idle kalau nggak ada hit
    // selama IdleAfter - timer lalu berhenti di waktu hit terakhir.
    static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(5);
    DateTime? _sessionStart;
    DateTime _lastHit;
    bool _paused;
    DateTime _pauseStart;

    public MainWindow()
    {
        InitializeComponent();
        PlayerList.ItemsSource = _players;
        TargetList.ItemsSource = _targets;

        _engine.HitDetected += OnHit;
        _engine.StatusChanged += OnStatus;
        _engine.PartyUpdated += OnPartyUpdated;
        _engine.SelfNameDetected += OnSelfNameDetected;
        // Engine mencatat SEMUA pemain; penyaringan party dilakukan di tampilan
        // (IsShownToken) supaya tombol "di luar party" bisa on/off dan berlaku mundur.
        // Default tetap cuma party: diri sendiri + nama yang ada di roster party.
        _engine.FilterToPartyOnly = false;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => RefreshDisplay();
        timer.Start();

        Loaded += async (_, _) =>
        {
            if (GameDir.Path is null) AskGameDir(firstRun: true);
            await LoadGameData();
            string? demo = Environment.GetEnvironmentVariable("DPSMETER_DEMO");
            string? replay = Environment.GetEnvironmentVariable("DPSMETER_REPLAY");
            if (demo != null) FeedDemoData(openBreakdown: demo == "breakdown", openTargets: demo == "target");
            else if (replay != null) _engine.StartReplay(replay);
            else if (!_engine.Start() && _engine.NpcapMissing) ShowNpcapHelp();
            if (Environment.GetEnvironmentVariable("DPSMETER_TAB") == "target") ShowTab(target: true);
            if (demo == "targetdrill" && _targets.Count > 0) OpenTarget(_targets[0]);
            UpdateStatusText();
        };
        Closing += (_, _) => _engine.Stop();
    }

    // ---- Plug & play: data game + Npcap ----

    /// Muat nama skill / monster / item dari folder game (bisa diulang setelah ganti folder).
    async System.Threading.Tasks.Task LoadGameData()
    {
        _skillStatus = await System.Threading.Tasks.Task.Run(() =>
            $"Folder game: {GameDir.Path ?? "belum ketemu"}\n" +
            SkillNames.LoadFromGame() + "\n" + MonsterNames.LoadFromGame() + "\n" + ItemNames.LoadFromGame() + "\n" +
            MonsterDrops.LoadFromGame());
        UpdateStatusText();
    }

    /// Minta user pilih folder Seal Online (yang berisi folder etc). True kalau folder valid dipilih.
    bool AskGameDir(bool firstRun)
    {
        if (firstRun)
            MessageBox.Show(this,
                "Folder instalasi Seal Online tidak ketemu otomatis.\n\n" +
                "Pilih folder game (yang berisi SO3DPlus.exe dan folder etc) supaya nama skill, monster, dan item bisa tampil.\n" +
                "Meter tetap bisa menghitung damage walaupun ini dilewati.",
                "Seal DPS Meter", MessageBoxButton.OK, MessageBoxImage.Information);

        while (true)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pilih folder Seal Online" };
            if (dlg.ShowDialog(this) != true) return false;
            if (GameDir.TrySet(dlg.FolderName)) return true;
            if (MessageBox.Show(this, "Folder itu bukan folder Seal Online (tidak ada etc\\etc.SPAK). Pilih lagi?",
                    "Seal DPS Meter", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;
        }
    }

    async void GameDirButton_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        if (AskGameDir(firstRun: false)) await LoadGameData();
    }

    void ShowNpcapHelp()
    {
        var answer = MessageBox.Show(this,
            "Npcap belum terpasang. Meter butuh Npcap untuk membaca paket game.\n\n" +
            "Buka halaman download Npcap sekarang?\n" +
            "(Install dengan pilihan default, lalu buka ulang meter.)",
            "Seal DPS Meter", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://npcap.com/#download") { UseShellExecute = true });
    }

    void OnHit(HitEvent hit)
    {
        Dispatcher.Invoke(() =>
        {
            if (_paused) return;

            var now = DateTime.Now;

            if (!_byToken.TryGetValue(hit.AttackerToken, out var stat))
            {
                string name = hit.IsSelf
                    ? (_engine.SelfName ?? "Kamu")
                    : _engine.NameOf(hit.AttackerToken) ?? $"Player {_nextColor + 1}";
                // Karakter yang sama dengan entity ID baru -> pakai baris yang sudah ada.
                stat = FindByName(name);
                if (stat == null)
                {
                    stat = new PlayerStat(name, _nextColor);
                    _nextColor++;
                    _players.Add(stat);
                }
                _byToken[hit.AttackerToken] = stat;
            }

            // Hit yang lagi disembunyikan (di luar party, tombol OFF) nggak ikut
            // memulai / memperpanjang timer sesi.
            if (IsShownStat(stat))
            {
                _sessionStart ??= now;
                _lastHit = now;
            }

            stat.AddHit(hit.Label, hit.Damage, now);

            if (!_targetById.TryGetValue(hit.TargetId, out var target))
            {
                target = new TargetStat(hit.TargetId);
                _targetById[hit.TargetId] = target;
                _targets.Add(target);
            }
            target.AddHit(hit.Damage, hit.TargetHpAfter, now);
            target.PlayerFor(stat).AddHit(hit.Label, hit.Damage, now);
        });
    }

    void OnPartyUpdated()
    {
        Dispatcher.Invoke(() =>
        {
            // Update nama yang udah tercatat lewat hit
            foreach (var kv in _byToken)
            {
                if (_engine.PartyMembers.TryGetValue(kv.Key, out var name))
                    kv.Value.Name = name;
            }

            // Tampilkan langsung semua anggota party walau belum ada damage sama sekali
            // (skip kalau namanya = nama kamu sendiri, biar nggak dobel sama bucket self)
            // Roster/broadcast bisa berisi >1 entity ID untuk 1 nama (ID lama + ID baru
            // setelah masuk dungeon) -> tetap 1 baris per nama.
            foreach (var kv in _engine.PartyMembers)
            {
                if (_byToken.ContainsKey(kv.Key)) continue;
                var existing = FindByName(kv.Value) ?? (kv.Value == _engine.SelfName ? SelfStat : null);
                if (existing != null)
                {
                    _byToken[kv.Key] = existing;
                    continue;
                }
                if (kv.Value == _engine.SelfName) continue;
                var stat = new PlayerStat(kv.Value, _nextColor);
                _nextColor++;
                stat.FirstHit = DateTime.Now;
                stat.LastHit = DateTime.Now;
                _byToken[kv.Key] = stat;
                _players.Add(stat);
            }
            MergeDuplicateNames();
        });
    }

    string _engineStatus = "...";
    string _skillStatus = "";

    void OnStatus(string status)
    {
        Dispatcher.Invoke(() =>
        {
            _engineStatus = status;
            UpdateStatusText();
        });
    }

    void UpdateStatusText() =>
        StatusText.Text = $"{_engineStatus}\n{_skillStatus}\nMonster terdeteksi: {_engine.KnownEntityCount}" +
                          "\nIkon job: game-icons.net (Lorc, Delapouite) - CC BY 3.0";

    void RefreshDisplay()
    {
        UpdateTimer();
        if (MenuPopup.IsOpen) UpdateStatusText();
        if (_players.Count == 0) return;

        // Job bisa baru ke-detect belakangan (roster party / broadcast CC1B), dan
        // nama pemain juga bisa berubah (auto-detect nama sendiri).
        foreach (var p in _players.Concat(_targets.SelectMany(t => t.Players)))
            p.Job = _engine.JobOf(p.Name) ?? p.Job;

        foreach (var kv in _byToken)
        {
            // Nama pemain di luar party bisa baru ketahuan belakangan (broadcast CC1B).
            if (kv.Key != CaptureEngine.SELF_TOKEN && kv.Value.Name.StartsWith("Player ") && _engine.NameOf(kv.Key) is { } live)
                kv.Value.Name = live;
        }
        MergeDuplicateNames();
        foreach (var p in _players) p.IsShown = IsShownStat(p);

        RefreshPlayers(_players);
        RefreshTargets();
        if (_scope != null)
        {
            // Nama pemain di tampilan per-target ikut nama global (bisa berubah
            // belakangan: auto-detect nama sendiri / roster party).
            foreach (var kv in _scope.PlayersByGlobal)
                if (kv.Value.Name != kv.Key.Name)
                    kv.Value.Name = kv.Key.Name;
            RefreshPlayers(_scope.Players);
            ScopeName.Text = _scope.Name;
            ScopeDmg.Text = NumberFormat.Abbrev(_scope.Damage);
            ScopeDmg.ToolTip = NumberFormat.Full(_scope.Damage);
        }

        double totalDamage = _players.Where(p => p.IsShown).Sum(p => p.Damage);
        double totalDps = totalDamage / Math.Max(SessionDuration().TotalSeconds, 1.0);

        TotalDmgText.Text = NumberFormat.Abbrev(totalDamage);
        TotalDmgText.ToolTip = NumberFormat.Full(totalDamage);
        TotalDpsText.Text = NumberFormat.Abbrev(totalDps);
        TotalDpsText.ToolTip = NumberFormat.Full(totalDps);
    }

    /// Hitung ulang bar/D% (relatif ke isi koleksi ini) + urutkan dari damage terbesar.
    static void RefreshPlayers(ObservableCollection<PlayerStat> players)
    {
        if (players.Count == 0) return;
        var shown = players.Where(p => p.IsShown).ToList();
        double maxDamage = shown.Count > 0 ? shown.Max(p => p.Damage) : 0;
        double sumDamage = shown.Sum(p => p.Damage);
        foreach (var p in players)
        {
            p.PercentOfTop = maxDamage > 0 ? p.Damage / maxDamage * 100.0 : 0;
            p.PercentOfTotal = sumDamage > 0 ? p.Damage / sumDamage * 100.0 : 0;
            p.Refresh();
        }

        var sorted = players.OrderByDescending(p => p.Damage).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int currentIndex = players.IndexOf(sorted[i]);
            if (currentIndex != i) players.Move(currentIndex, i);
        }
    }

    void RefreshTargets()
    {
        if (_targets.Count == 0) return;
        foreach (var t in _targets) t.ApplyFilter();
        double top = _targets.Max(t => t.Damage);
        double sum = _targets.Sum(t => t.Damage);
        foreach (var t in _targets)
        {
            // Nama bisa baru ke-detect belakangan (paket entity list lewat setelah hit pertama).
            t.MonsterName ??= t.EntityId != 0 ? _engine.TargetName(t.EntityId) : null;
            t.TypeId ??= t.EntityId != 0 ? _engine.TargetType(t.EntityId) : null;
            t.PercentOfTotal = sum > 0 ? t.Damage / sum * 100.0 : 0;
            t.BarScale = top > 0 ? t.Damage / top : 0;
            t.Refresh();
        }
        var sorted = _targets.OrderByDescending(t => t.Damage).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int idx = _targets.IndexOf(sorted[i]);
            if (idx != i) _targets.Move(idx, i);
        }
    }

    // ---- Tab footer: DPS (party + breakdown) / TARGET ----

    void TabDps_Click(object sender, RoutedEventArgs e) => ShowTab(target: false);
    void TabTarget_Click(object sender, RoutedEventArgs e) => ShowTab(target: true);

    void ShowTab(bool target)
    {
        SetScope(null);
        TargetView.Visibility = target ? Visibility.Visible : Visibility.Collapsed;
        DpsTabContent.Visibility = target ? Visibility.Collapsed : Visibility.Visible;
        TabDps.Tag = target ? null : "active";
        TabTarget.Tag = target ? "active" : null;
    }

    // ---- Drill-down target: klik monster -> tabel pemain khusus monster itu ----

    /// scope null = semua target (tab DPS biasa), selain itu cuma damage ke target tsb.
    void SetScope(TargetStat? target)
    {
        _scope = target;
        ShowPartyView();
        PlayerList.ItemsSource = target?.Players ?? _players;
        ScopeBar.Visibility = target != null ? Visibility.Visible : Visibility.Collapsed;
        if (target != null) RefreshDisplay();
    }

    void TargetRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TargetStat target) OpenTarget(target);
    }

    void OpenTarget(TargetStat target)
    {
        TargetView.Visibility = Visibility.Collapsed;
        DpsTabContent.Visibility = Visibility.Visible;
        TabDps.Tag = null;
        TabTarget.Tag = "active"; // masih "di dalam" tab TARGET
        SetScope(target);
    }

    /// Balik dari tabel pemain per-target ke daftar target.
    void BackToTargets() => ShowTab(target: true);

    void ScopeBack_Click(object sender, RoutedEventArgs e) => BackToTargets();

    void PartyView_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_scope != null) BackToTargets();
    }

    /// Durasi sesi: dari hit pertama sampai sekarang, berhenti di hit terakhir
    /// kalau sudah idle (nggak ada hit > IdleAfter), beku selama pause.
    TimeSpan SessionDuration()
    {
        if (_sessionStart is null) return TimeSpan.Zero;
        DateTime end = _paused ? _pauseStart : DateTime.Now;
        if (end - _lastHit > IdleAfter) end = _lastHit;
        return end - _sessionStart.Value;
    }

    void UpdateTimer() => TimerText.Text = NumberFormat.Duration(SessionDuration());

    void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_paused)
        {
            _paused = true;
            _pauseStart = DateTime.Now;
        }
        else
        {
            // Geser semua timestamp maju sebesar lama pause, supaya waktu pause
            // nggak ikut kehitung ke durasi/DPS.
            var gap = DateTime.Now - _pauseStart;
            if (_sessionStart is not null)
            {
                _sessionStart += gap;
                _lastHit += gap;
            }
            foreach (var p in _players.Concat(_targets.SelectMany(t => t.Players)))
            {
                p.FirstHit += gap;
                p.LastHit += gap;
            }
            _paused = false;
        }
        // Ikon Segoe MDL2: E768 = Play, E769 = Pause (ditulis sebagai kode biar file tetap ASCII)
        PauseButton.Content = ((char)(_paused ? 0xE768 : 0xE769)).ToString();
        PauseButton.ToolTip = _paused ? "Resume" : "Pause";
        PausedBadge.Visibility = _paused ? Visibility.Visible : Visibility.Collapsed;
    }

    /// Khusus development (env DPSMETER_DEMO=1 / =breakdown): isi data palsu
    /// tanpa capture, buat cek tampilan UI tanpa harus masuk game.
    void FeedDemoData(bool openBreakdown, bool openTargets)
    {
        var rnd = new Random(7);
        _engine.RememberEntityType(5314, 2237); // [Look A Like]Giant R. Rabbit
        _engine.RememberEntityType(5315, 2239);
        (uint token, bool self, string label, double dmg, int n, uint target)[] demo =
        {
            (CaptureEngine.SELF_TOKEN, true, "Deathly Slash", 57_300_000, 6, 5314),
            (CaptureEngine.SELF_TOKEN, true, "Soul Breaker", 34_600_000, 8, 5315),
            (CaptureEngine.SELF_TOKEN, true, "Vital Attack", 14_200_000, 4, 777),
            (CaptureEngine.SELF_TOKEN, true, "Auto", 80_000, 40, 5314),
            (101, false, "Skill", 9_000_000, 12, 0),
            (101, false, "Auto", 12_500, 30, 5314),
        };
        foreach (var d in demo)
            for (int i = 0; i < d.n; i++)
                OnHit(new HitEvent(d.token, d.label == "Auto" ? "Auto" : "Skill", d.dmg * (0.95 + rnd.NextDouble() * 0.1), d.self, d.label,
                                   d.target, d.target == 0 ? 0 : 1_000_000_000 - i * d.dmg));
        _sessionStart = DateTime.Now.AddSeconds(-42);
        foreach (var p in _players) p.FirstHit = _sessionStart.Value;
        RefreshDisplay();
        if (openTargets) ShowTab(target: true);
        if (openBreakdown)
        {
            BreakdownView.DataContext = _players[0];
            PartyView.Visibility = Visibility.Collapsed;
            BreakdownView.Visibility = Visibility.Visible;
        }
    }

    // ---- Navigasi party <-> breakdown skill (kayak LOA: klik pemain, klik kanan balik) ----

    void PlayerRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlayerStat player) return;
        BreakdownView.DataContext = player;
        PartyView.Visibility = Visibility.Collapsed;
        BreakdownView.Visibility = Visibility.Visible;
    }

    // ---- Equip 1 pemain (dari breakdown) ----

    void EquipButton_Click(object sender, RoutedEventArgs e)
    {
        if (BreakdownView.DataContext is not PlayerStat player) return;
        var equip = _engine.EquipmentOf(player.Name);
        EquipView.DataContext = player;
        EquipList.ItemsSource = equip;
        bool isSelf = _byToken.TryGetValue(CaptureEngine.SELF_TOKEN, out var self) && self == player;
        EquipEmptyText.Text = isSelf
            ? "Equip milikmu sendiri belum bisa dibaca - server cuma mengirimnya saat masuk game, di paket yang berbeda."
            : "Equip belum ketahuan: data karakter ini dikirim server waktu dia masuk area pandangmu. Tunggu sampai dia terlihat lagi.";
        EquipEmptyText.Visibility = equip == null ? Visibility.Visible : Visibility.Collapsed;
        BreakdownView.Visibility = Visibility.Collapsed;
        EquipView.Visibility = Visibility.Visible;
    }

    void ShowBreakdownFromEquip()
    {
        EquipView.Visibility = Visibility.Collapsed;
        EquipView.DataContext = null;
        BreakdownView.Visibility = Visibility.Visible;
    }

    void EquipBack_Click(object sender, RoutedEventArgs e) => ShowBreakdownFromEquip();

    void EquipView_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => ShowBreakdownFromEquip();

    // ---- Drop list monster (tombol DROP di bar target) ----

    void DropButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scope is null) return;
        if (DropView.Visibility == Visibility.Visible) { ShowPartyView(); return; }
        _scope.TypeId ??= _scope.EntityId != 0 ? _engine.TargetType(_scope.EntityId) : null;
        var drops = _scope.TypeId is uint type ? MonsterDrops.Of(type) : null;
        DropList.ItemsSource = drops;
        DropEmptyText.Text = _scope.TypeId is null
            ? "Jenis monster ini belum ketahuan: server mengirimnya waktu monster masuk area pandangmu (sebelum meter nyala = tidak kebaca)."
            : drops is null ? "Monster ini tidak ada di data drop game."
            : "Monster ini tidak punya drop di data game.";
        DropEmptyText.Visibility = drops is { Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;
        PartyView.Visibility = Visibility.Collapsed;
        BreakdownView.Visibility = Visibility.Collapsed;
        EquipView.Visibility = Visibility.Collapsed;
        DropView.Visibility = Visibility.Visible;
    }

    void DropView_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => ShowPartyView();

    void ShowPartyView()
    {
        DropView.Visibility = Visibility.Collapsed;
        EquipView.Visibility = Visibility.Collapsed;
        EquipView.DataContext = null;
        BreakdownView.Visibility = Visibility.Collapsed;
        BreakdownView.DataContext = null;
        PartyView.Visibility = Visibility.Visible;
    }

    void BackButton_Click(object sender, RoutedEventArgs e) => ShowPartyView();

    void BreakdownView_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => ShowPartyView();

    void OutsidePartyButton_Click(object sender, RoutedEventArgs e)
    {
        _showOutsideParty = !_showOutsideParty;
        OutsidePartyButton.ToolTip = _showOutsideParty
            ? "Damage di luar party: ON (semua pemain)"
            : "Damage di luar party: OFF (cuma party)";
        // ON = oranye (sama dengan badge), OFF = balik ke warna default style IconButton.
        if (_showOutsideParty)
            OutsidePartyButton.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xA6, 0x23));
        else
            OutsidePartyButton.ClearValue(ForegroundProperty);
        AllBadge.Visibility = _showOutsideParty ? Visibility.Visible : Visibility.Collapsed;
        RefreshDisplay();
    }

    void MenuButton_Click(object sender, RoutedEventArgs e) => MenuPopup.IsOpen = !MenuPopup.IsOpen;

    void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    void TransparentCheck_Changed(object sender, RoutedEventArgs e)
    {
        // Sama kayak LOA: 95% gelap normal, 25% di mode transparan.
        RootBorder.Background = new System.Windows.Media.SolidColorBrush(
            TransparentCheck.IsChecked == true
                ? System.Windows.Media.Color.FromArgb(0x40, 0x17, 0x17, 0x17)
                : System.Windows.Media.Color.FromArgb(0xF2, 0x17, 0x17, 0x17));
    }

    void OnSelfNameDetected()
    {
        Dispatcher.Invoke(() => ApplySelfName(_engine.SelfName!));
    }

    void SelfNameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        string name = SelfNameBox.Text.Trim();
        if (name.Length == 0) return;
        ApplySelfName(name);
    }

    void ApplySelfName(string name)
    {
        // Rename bucket "Kamu" jadi nama asli, lalu gabung baris lain dengan nama
        // yang sama (placeholder roster / hit lewat entity ID kamu) ke baris diri sendiri.
        if (SelfStat is { } selfStat)
        {
            selfStat.Name = name;
            MergeDuplicateNames();
        }
    }

    void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scope != null) ShowTab(target: true);
        ShowPartyView();
        _players.Clear();
        _byToken.Clear();
        _targets.Clear();
        _targetById.Clear();
        _nextColor = 0;
        _sessionStart = null;
        TotalDmgText.Text = "0";
        TotalDmgText.ToolTip = null;
        TotalDpsText.Text = "0";
        TotalDpsText.ToolTip = null;
        UpdateTimer();
        _engine.ClearParty();
        SelfNameBox.Text = "";
    }
}
