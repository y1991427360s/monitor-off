using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using static MonitorOff.Native;

namespace MonitorOff;

public sealed class Mon
{
    public string Key = "";
    public string Name = "";
    public string Gdi = "";
    public int X, Y, W, H;
    public bool Primary;
    public bool Active;      // 接入桌面
    public bool? Ddc;        // null = 还没探测
    public bool Off;
    public bool Virtual;     // 推测为虚拟屏/诱骗器（无 DDC）
    public int Index;
    public int DX, DY, DW, DH;   // 布局图用的“原始布局”坐标
    public bool Physical;        // 曾探测到支持 DDC（实体屏）
    public bool CanDdc => Ddc == true;
}

/// <summary>所有显示器操作。调用方应在后台线程调用，内部串行。</summary>
public static class MonitorService
{
    static readonly object OpLock = new();

    // ---------------- 枚举 ----------------
    static Dictionary<string, (RECT r, bool primary, IntPtr h)> DesktopMonitors()
    {
        var d = new Dictionary<string, (RECT, bool, IntPtr)>(StringComparer.OrdinalIgnoreCase);
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr p) =>
        {
            var mi = new MONITORINFOEX { cb = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi)) d[mi.dev] = (mi.mon, (mi.flags & 1) != 0, h);
            return true;
        }, IntPtr.Zero);
        return d;
    }

    static string MakerCode(ushort id)
    {
        int v = (id >> 8) | ((id & 0xFF) << 8); // 大端
        char c1 = (char)('A' - 1 + ((v >> 10) & 0x1F));
        char c2 = (char)('A' - 1 + ((v >> 5) & 0x1F));
        char c3 = (char)('A' - 1 + (v & 0x1F));
        var s = new string(new[] { c1, c2, c3 });
        return s.All(char.IsLetter) ? s : "";
    }

    static string NiceName(TARGET_DEVICE_NAME t)
    {
        var name = (t.friendlyName ?? "").Trim();
        var maker = MakerCode(t.edidManufactureId);
        var brand = maker == "DEL" ? "DELL" : maker;
        if (name.Length == 0) return brand.Length > 0 ? brand + " 显示器" : "显示器";
        if (brand.Length > 0 && !name.StartsWith(brand, StringComparison.OrdinalIgnoreCase)) name = brand + " " + name;
        return name;
    }

    public static List<Mon> Enumerate()
    {
        var list = new List<Mon>();
        if (GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out uint np, out uint nm) != 0) return list;
        var paths = new PATH_INFO[np];
        var modes = new MODE_INFO[nm];
        if (QueryDisplayConfig(QDC_ALL_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return list;

        var desk = DesktopMonitors();
        var settings = Store.Load();
        var offs = Store.OffSnapshot();
        var seen = new Dictionary<string, Mon>();

        for (int i = 0; i < np; i++)
        {
            var p = paths[i];
            if (p.target.targetAvailable == 0) continue;
            string tkey = $"{p.target.adapterId.Low}:{p.target.adapterId.High}:{p.target.id}";
            bool active = (p.flags & 1) != 0;
            if (!seen.TryGetValue(tkey, out var m))
            {
                var tn = new TARGET_DEVICE_NAME();
                tn.header.type = 2; tn.header.size = Marshal.SizeOf<TARGET_DEVICE_NAME>();
                tn.header.adapterId = p.target.adapterId; tn.header.id = p.target.id;
                if (DisplayConfigGetDeviceInfo(ref tn) != 0 || string.IsNullOrEmpty(tn.devicePath)) continue;
                m = new Mon
                {
                    Key = tn.devicePath.ToLowerInvariant(),
                    Name = NiceName(tn),
                    Virtual = tn.outputTechnology is 16 or 17 // 间接显示（ToDesk 等虚拟显示驱动）
                };
                seen[tkey] = m;
            }
            if (active && !m.Active)
            {
                var sn = new SOURCE_DEVICE_NAME();
                sn.header.type = 1; sn.header.size = Marshal.SizeOf<SOURCE_DEVICE_NAME>();
                sn.header.adapterId = p.source.adapterId; sn.header.id = p.source.id;
                if (DisplayConfigGetDeviceInfo(ref sn) == 0 && desk.TryGetValue(sn.gdiName, out var dm))
                {
                    m.Active = true; m.Gdi = sn.gdiName; m.Primary = dm.primary;
                    m.X = dm.r.L; m.Y = dm.r.T; m.W = dm.r.R - dm.r.L; m.H = dm.r.B - dm.r.T;
                }
            }
        }

        foreach (var m in seen.Values)
        {
            var rec = offs.FirstOrDefault(o => o.Key == m.Key);
            if (!m.Active)
            {
                // 未接入桌面：本工具断开的 → 用记录的位置；间接/虚拟驱动的空闲口不显示
                if (rec == null && m.Virtual) continue;
                if (rec != null) { m.X = rec.X; m.Y = rec.Y; m.W = rec.W; m.H = rec.H; m.Name = rec.Name; }
                else { m.X = int.MaxValue / 2; m.W = 1920; m.H = 1080; }
                m.Off = true; m.Ddc = false;
            }
            else
            {
                if (settings.DdcCache.TryGetValue(m.Key, out var ddc)) m.Ddc = ddc;
                m.Off = rec != null && rec.Method == "ddc";
            }
            list.Add(m);
        }

        // 已经重新接入的“断开”记录作废
        var stale = offs.Where(o => o.Method == "detach" && list.Any(m => m.Key == o.Key && m.Active)).Select(o => o.Key).ToList();
        if (stale.Count > 0) Store.Update(s => s.Off.RemoveAll(o => stale.Contains(o.Key)));

        // 原始布局：没有断开任何屏时记下当前布局；断开期间沿用记下的布局，位置和编号都不变
        bool anyDetached = list.Any(m => !m.Active);
        if (!anyDetached && list.Count > 0)
        {
            var snap = list.ToDictionary(m => m.Key, m => new[] { m.X, m.Y, m.W, m.H });
            bool same = settings.Home.Count == snap.Count && snap.All(kv => settings.Home.TryGetValue(kv.Key, out var v) && v.SequenceEqual(kv.Value));
            if (!same) Store.Update(s => s.Home = snap);
        }
        var home = Store.Load().Home;
        int extraX = home.Values.Select(v => v[0] + v[2]).DefaultIfEmpty(0).Max() + 80;
        foreach (var m in list)
        {
            if (home.TryGetValue(m.Key, out var v)) { m.DX = v[0]; m.DY = v[1]; m.DW = v[2]; m.DH = v[3]; }
            else if (m.Active && !anyDetached) { m.DX = m.X; m.DY = m.Y; m.DW = m.W; m.DH = m.H; }
            else { m.DX = extraX; m.DY = 0; m.DW = m.W > 0 ? m.W : 1920; m.DH = m.H > 0 ? m.H : 1080; extraX += m.DW + 80; }
            m.Physical = settings.DdcCache.TryGetValue(m.Key, out var d) && d;
        }

        list.Sort((a, b) => a.DX != b.DX ? a.DX.CompareTo(b.DX) : a.DY.CompareTo(b.DY));
        for (int i = 0; i < list.Count; i++) list[i].Index = i + 1;
        return list;
    }

    // ---------------- DDC ----------------
    static IntPtr HmonOf(string gdi)
        => DesktopMonitors().TryGetValue(gdi, out var d) ? d.h : IntPtr.Zero;

    static bool WithPhysical(string gdi, Func<IntPtr, bool> act)
    {
        var h = HmonOf(gdi);
        if (h == IntPtr.Zero) return false;
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(h, out uint n) || n == 0) return false;
        var a = new PHYSICAL_MONITOR[n];
        if (!GetPhysicalMonitorsFromHMONITOR(h, n, a)) return false;
        bool ok = false;
        try { foreach (var p in a) if (act(p.h)) ok = true; }
        finally { DestroyPhysicalMonitors(n, a); }
        return ok;
    }

    public static bool ProbeDdc(Mon m)
        => m.Active && WithPhysical(m.Gdi, p => GetVCPFeatureAndVCPFeatureReply(p, 0xD6, IntPtr.Zero, out _, out _));

    /// <summary>探测还没缓存的显示器是否支持 DDC，结果写入缓存。返回是否有变化。</summary>
    public static bool ProbeMissing(List<Mon> mons)
    {
        bool changed = false;
        foreach (var m in mons.Where(m => m.Active && m.Ddc == null))
        {
            bool ok;
            lock (OpLock) ok = ProbeDdc(m);
            m.Ddc = ok; changed = true;
            var key = m.Key;
            Store.Update(s => s.DdcCache[key] = ok);
        }
        return changed;
    }

    static bool DdcPower(string gdi, uint val)
    {
        for (int i = 0; i < 3; i++)
        {
            if (WithPhysical(gdi, p => SetVCPFeature(p, 0xD6, val))) return true;
            Thread.Sleep(80);
        }
        return false;
    }


    // ---------------- 拓扑：断开 / 接回（SetDisplayConfig 精确指定） ----------------
    static (PATH_INFO[] p, MODE_INFO[] m) Query(uint flags)
    {
        for (int tries = 0; tries < 3; tries++)
        {
            if (GetDisplayConfigBufferSizes(flags, out uint np, out uint nm) != 0) break;
            var p = new PATH_INFO[np]; var m = new MODE_INFO[nm];
            int r = QueryDisplayConfig(flags, ref np, p, ref nm, m, IntPtr.Zero);
            if (r == 0) return (p.Take((int)np).ToArray(), m.Take((int)nm).ToArray());
            if (r != 122) break; // ERROR_INSUFFICIENT_BUFFER：拓扑刚变，重试
        }
        return (Array.Empty<PATH_INFO>(), Array.Empty<MODE_INFO>());
    }

    static readonly Dictionary<string, string> TargetKeyCache = new();
    static string KeyOf(PATH_TARGET t)
    {
        string id = $"{t.adapterId.Low}:{t.adapterId.High}:{t.id}";
        if (TargetKeyCache.TryGetValue(id, out var k)) return k;
        var tn = new TARGET_DEVICE_NAME();
        tn.header.type = 2; tn.header.size = Marshal.SizeOf<TARGET_DEVICE_NAME>();
        tn.header.adapterId = t.adapterId; tn.header.id = t.id;
        k = DisplayConfigGetDeviceInfo(ref tn) == 0 ? (tn.devicePath ?? "").ToLowerInvariant() : "";
        TargetKeyCache[id] = k;
        return k;
    }

    static bool SameLuid(LUID a, LUID b) => a.Low == b.Low && a.High == b.High;

    static int Apply(PATH_INFO[] paths, MODE_INFO[] modes)
        => SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
            SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);

    static void SaveSnapshot()
    {
        var (p, m) = Query(QDC_ONLY_ACTIVE_PATHS);
        if (p.Length == 0) return;
        string ps = Convert.ToBase64String(MemoryMarshal.AsBytes(p.AsSpan()).ToArray());
        string ms = Convert.ToBase64String(MemoryMarshal.AsBytes(m.AsSpan()).ToArray());
        Store.Update(s => { s.SnapPaths = ps; s.SnapModes = ms; });
    }

    static int ApplySnapshot()
    {
        var s = Store.Load();
        if (string.IsNullOrEmpty(s.SnapPaths) || string.IsNullOrEmpty(s.SnapModes)) return -1;
        try
        {
            var p = MemoryMarshal.Cast<byte, PATH_INFO>(Convert.FromBase64String(s.SnapPaths)).ToArray();
            var m = MemoryMarshal.Cast<byte, MODE_INFO>(Convert.FromBase64String(s.SnapModes)).ToArray();
            return Apply(p, m); // 重启后显卡 LUID 会变，这里会失败，调用方再走兜底
        }
        catch { return -2; }
    }

    /// <summary>断开指定显示器。主屏被断开时，把 preferPrimary（或剩下的第一台）挪到 (0,0) 当主屏。</summary>
    static int DetachKeys(HashSet<string> keys, string? preferPrimary)
    {
        var (p, m) = Query(QDC_ONLY_ACTIVE_PATHS);
        var keep = p.Where(x => !keys.Contains(KeyOf(x.target))).ToArray();
        if (keep.Length == 0) return -1;
        if (keep.Length == p.Length) return 0;

        // 只保留被引用的模式，并重排索引
        var modes = new List<MODE_INFO>();
        var map = new Dictionary<uint, uint>();
        uint Remap(uint idx)
        {
            if (idx == MODE_IDX_INVALID || idx >= m.Length) return MODE_IDX_INVALID;
            if (!map.TryGetValue(idx, out var n)) { n = (uint)modes.Count; modes.Add(m[idx]); map[idx] = n; }
            return n;
        }
        for (int i = 0; i < keep.Length; i++)
        {
            keep[i].source.modeInfoIdx = Remap(keep[i].source.modeInfoIdx);
            keep[i].target.modeInfoIdx = Remap(keep[i].target.modeInfoIdx);
        }

        var arr = modes.ToArray();
        var srcIdx = keep.Select(x => x.source.modeInfoIdx).Where(i => i != MODE_IDX_INVALID).Distinct().ToList();
        bool hasOrigin = srcIdx.Any(i => arr[i].srcX == 0 && arr[i].srcY == 0);
        if (!hasOrigin && srcIdx.Count > 0)
        {
            uint pi = srcIdx[0];
            foreach (var x in keep)
                if (preferPrimary != null && KeyOf(x.target) == preferPrimary && x.source.modeInfoIdx != MODE_IDX_INVALID) { pi = x.source.modeInfoIdx; break; }
            int dx = arr[pi].srcX, dy = arr[pi].srcY;
            foreach (var i in srcIdx) { arr[i].srcX -= dx; arr[i].srcY -= dy; }
        }
        return Apply(keep, arr);
    }

    /// <summary>接回指定显示器：给它挑一个空闲的源，交给 Windows 选模式。</summary>
    static int AttachKeys(HashSet<string> keys)
    {
        var (ap, am) = Query(QDC_ONLY_ACTIVE_PATHS);
        var (all, _) = Query(QDC_ALL_PATHS);
        var paths = ap.ToList();
        var activeKeys = ap.Select(x => KeyOf(x.target)).ToHashSet();
        var used = ap.Select(x => (x.source.adapterId.Low, x.source.adapterId.High, x.source.id)).ToHashSet();
        bool added = false;
        foreach (var key in keys)
        {
            if (activeKeys.Contains(key)) continue;
            foreach (var c in all)
            {
                if (c.target.targetAvailable == 0 || KeyOf(c.target) != key) continue;
                if (!SameLuid(c.source.adapterId, c.target.adapterId)) continue;
                if (used.Contains((c.source.adapterId.Low, c.source.adapterId.High, c.source.id))) continue;
                var n = c;
                n.flags = 1; // DISPLAYCONFIG_PATH_ACTIVE
                n.source.modeInfoIdx = MODE_IDX_INVALID;
                n.target.modeInfoIdx = MODE_IDX_INVALID;
                n.source.statusFlags = 0; n.target.statusFlags = 0;
                paths.Add(n);
                used.Add((c.source.adapterId.Low, c.source.adapterId.High, c.source.id));
                added = true;
                break;
            }
        }
        if (!added) return -3;
        return Apply(paths.ToArray(), am);
    }

    static void MoveTo(string gdi, int x, int y)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>(), dmFields = DM_POSITION, dmPositionX = x, dmPositionY = y };
        if (ChangeDisplaySettingsEx(gdi, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero) == 0)
            ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
    }

    // ---------------- 对外操作 ----------------
    /// <param name="forceDetach">true：支持 DDC 的屏也断开输出（远程模式）</param>
    public static List<string> TurnOff(IEnumerable<string> keys, bool forceDetach = false)
    {
        var msgs = new List<string>();
        lock (OpLock)
        {
            var all = Enumerate();
            var keySet = keys.ToHashSet();
            var targets = all.Where(m => keySet.Contains(m.Key) && m.Active).ToList();
            if (targets.Count == 0) { msgs.Add("所选显示器已经是关闭状态"); return msgs; }
            foreach (var m in targets.Where(m => m.Ddc == null)) m.Ddc = ProbeDdc(m);

            bool preferDetach = forceDetach || Store.Load().PreferDetach;
            var detach = targets.Where(m => !m.CanDdc || preferDetach).ToList();
            var ddc = targets.Where(m => m.CanDdc && !preferDetach).ToList();
            var remain = all.Where(m => m.Active && !detach.Contains(m)).ToList();

            if (detach.Count > 0 && remain.Count == 0)
            {
                if (!targets.Any(m => m.CanDdc))
                {
                    msgs.Add("全部显示器：系统待机（动键鼠即唤醒）");
                    Thread.Sleep(300);
                    PostMessage((IntPtr)0xFFFF, 0x0112, (IntPtr)0xF170, (IntPtr)2);
                    return msgs;
                }
                // 不能全部断开（至少留一台接在桌面上）：支持 DDC 的改用 DDC 待机
                foreach (var m in detach.Where(m => m.CanDdc).ToList()) { detach.Remove(m); ddc.Add(m); }
                remain = all.Where(m => m.Active && !detach.Contains(m)).ToList();
                if (remain.Count == 0) { msgs.Add("无法关闭：至少要留一台显示器"); return msgs; }
            }

            if (detach.Count > 0)
            {
                if (!Store.OffSnapshot().Any(o => o.Method == "detach")) SaveSnapshot();
                string? newPrimary = remain.FirstOrDefault(m => m.Primary)?.Key
                                     ?? remain.FirstOrDefault(m => !m.CanDdc)?.Key
                                     ?? remain.FirstOrDefault()?.Key;
                int r = DetachKeys(detach.Select(m => m.Key).ToHashSet(), newPrimary);
                if (r == 0)
                {
                    foreach (var m in detach)
                    {
                        var rec = new OffRecord { Key = m.Key, Name = m.Name, Method = "detach", X = m.X, Y = m.Y, W = m.W, H = m.H };
                        Store.Update(s => { s.Off.RemoveAll(o => o.Key == rec.Key); s.Off.Add(rec); });
                        msgs.Add($"{m.Index} 号 {m.Name}：已断开输出");
                    }
                }
                else msgs.Add($"断开输出失败（{r}）");
            }

            if (ddc.Count > 0)
            {
                if (detach.Count > 0) { Thread.Sleep(800); all = Enumerate(); }
                foreach (var t in ddc)
                {
                    var m = all.FirstOrDefault(x => x.Key == t.Key && x.Active);
                    if (m != null && DdcPower(m.Gdi, 4))
                    {
                        var rec = new OffRecord { Key = m.Key, Name = m.Name, Method = "ddc", X = m.X, Y = m.Y, W = m.W, H = m.H };
                        Store.Update(s => { s.Off.RemoveAll(o => o.Key == rec.Key); s.Off.Add(rec); });
                        msgs.Add($"{t.Index} 号 {t.Name}：已关闭（DDC）");
                    }
                    else msgs.Add($"{t.Index} 号 {t.Name}：DDC 命令失败");
                }
            }
        }
        return msgs;
    }

    public static List<string> TurnOn(IEnumerable<string> keys, bool all = false)
    {
        var msgs = new List<string>();
        lock (OpLock)
        {
            var mons = Enumerate();
            var offs = Store.OffSnapshot();
            var keySet = all ? mons.Select(m => m.Key).Concat(offs.Select(o => o.Key)).ToHashSet() : keys.ToHashSet();

            // 1) 接回断开的输出：全部接回时优先原样套用快照，否则精确接回，最后兜底“扩展”
            var needAttach = mons.Where(m => keySet.Contains(m.Key) && !m.Active).ToList();
            if (needAttach.Count > 0)
            {
                bool full = mons.Where(m => !m.Active).All(m => keySet.Contains(m.Key));
                int r = -1; string how = "";
                if (full) { r = ApplySnapshot(); how = "快照"; }
                if (r != 0) { r = AttachKeys(needAttach.Select(m => m.Key).ToHashSet()); how = "接回"; }
                if (r != 0) { r = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_EXTEND); how = "扩展"; }
                Thread.Sleep(1200);
                var now = Enumerate();
                foreach (var t in needAttach)
                {
                    var m = now.FirstOrDefault(x => x.Key == t.Key && x.Active);
                    var rec = offs.FirstOrDefault(o => o.Key == t.Key);
                    if (m == null) { msgs.Add($"{t.Name}：接回失败（{r}），可按 Win+P 选「扩展」"); continue; }
                    if (how != "快照" && rec != null && rec.W > 0 && (m.X != rec.X || m.Y != rec.Y) && !m.Primary) MoveTo(m.Gdi, rec.X, rec.Y);
                    Store.Update(s => s.Off.RemoveAll(o => o.Key == t.Key));
                    msgs.Add($"{t.Name}：已恢复输出");
                }
                if (!Store.OffSnapshot().Any(o => o.Method == "detach")) Store.Update(s => { s.SnapPaths = null; s.SnapModes = null; });
                mons = Enumerate();
            }

            // 2) DDC 打开（待机时读回值不可靠，所以不判断，直接发）
            foreach (var m in mons.Where(m => keySet.Contains(m.Key) && m.Active))
            {
                bool recorded = offs.Any(o => o.Key == m.Key && o.Method == "ddc");
                if (m.Ddc == false && !recorded) continue;
                bool ok = DdcPower(m.Gdi, 1);
                Store.Update(s => s.Off.RemoveAll(o => o.Key == m.Key && o.Method == "ddc"));
                if (recorded || !all) msgs.Add($"{m.Index} 号 {m.Name}：{(ok ? "已打开" : "DDC 失败，请按显示器电源键")}");
            }

            PostMessage((IntPtr)0xFFFF, 0x0112, (IntPtr)0xF170, (IntPtr)(-1));
        }
        if (msgs.Count == 0) msgs.Add("显示器都已是开启状态");
        return msgs;
    }

    /// <summary>保持关闭：给记录为 DDC 关闭的显示器补发待机命令。</summary>
    public static void KeepOffTick()
    {
        var offs = Store.OffSnapshot().Where(o => o.Method == "ddc").ToList();
        if (offs.Count == 0) return;
        lock (OpLock)
        {
            var mons = Enumerate();
            foreach (var o in offs)
            {
                var m = mons.FirstOrDefault(x => x.Key == o.Key && x.Active);
                if (m != null) DdcPower(m.Gdi, 4);
            }
        }
    }

    public static bool AnyOff() => Store.OffSnapshot().Count > 0;
}
