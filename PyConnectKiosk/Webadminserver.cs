using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PyConnectKiosk;

/// <summary>
/// Lightweight embedded HTTP admin server.
/// Serves a browser-based admin SPA at http://&lt;kiosk-ip&gt;:&lt;port&gt;/
/// All state-mutating API calls require an X-Token header obtained via POST /api/login.
/// </summary>
public sealed class WebAdminServer : IDisposable
{
    private readonly HttpListener           _listener = new();
    private          CancellationTokenSource _cts     = new();
    private          string?                _token;

    public int  Port      { get; }
    public bool IsRunning { get; private set; }

    public WebAdminServer(int port)
    {
        Port = port;
        _listener.Prefixes.Add($"http://+:{port}/");
    }

    //Lifecycle

    public void Start()
    {
        if (IsRunning) return;
        _cts      = new CancellationTokenSource();
        _listener.Start();
        IsRunning = true;
        Task.Run(() => AcceptLoop(_cts.Token));
    }

    public void Stop()
    {
        IsRunning = false;
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
    }

    public void Dispose() => Stop();

    // Accept loop
    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleSafe(ctx), CancellationToken.None);
            }
            catch { break; }
        }
    }

    //Request dispatcher

    private async Task HandleSafe(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            res.Headers["Access-Control-Allow-Origin"]  = "*";
            res.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
            res.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Token";

            if (req.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            { res.StatusCode = 204; return; }

            string path   = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
            string method = req.HttpMethod.ToUpperInvariant();

            if ((path == "" || path == "/index.html") && method == "GET")
            { await SendHtml(res, AdminHtml()); return; }

            if (path == "/api/login" && method == "POST")
            { await ApiLogin(req, res); return; }

            if (!IsAuthed(req))
            { await SendJson(res, new { error = "Unauthorized" }, 401); return; }

            switch ((method, path))
            {
                case ("GET",    "/api/users"):           await ApiGetUsers(res);             break;
                case ("POST",   "/api/users"):           await ApiSaveUsers(req, res);       break;
                case ("GET",    "/api/racks"):           await ApiGetRacks(res);             break;

                case ("GET",    "/api/photos/escorts"):  await ApiListPhotos(res, FaceSettings.EscortFacesDir); break;
                case ("POST",   "/api/photos/escorts"):  await ApiUploadPhoto(req, res, FaceSettings.EscortFacesDir); break;
                case ("DELETE", "/api/photos/escorts"):  await ApiDeletePhoto(req, res, FaceSettings.EscortFacesDir); break;

                case ("GET",    "/api/photos/users"):    await ApiListPhotos(res, FaceSettings.UserFacesDir); break;
                case ("POST",   "/api/photos/users"):    await ApiUploadPhoto(req, res, FaceSettings.UserFacesDir); break;
                case ("DELETE", "/api/photos/users"):    await ApiDeletePhoto(req, res, FaceSettings.UserFacesDir); break;

                default: res.StatusCode = 404; break;
            }
        }
        catch (Exception ex)
        {
            try { await SendJson(res, new { error = ex.Message }, 500); } catch { /* ignore */ }
        }
        finally
        {
            try { res.Close(); } catch { /* ignore */ }
        }
    }

    //Auth 

    private bool IsAuthed(HttpListenerRequest req)
    {
        var tok = req.Headers["X-Token"];
        return tok != null && tok == _token;
    }

    private async Task ApiLogin(HttpListenerRequest req, HttpListenerResponse res)
    {
        var body = await ReadJsonAsync(req);
        if (body.TryGetProperty("pin", out var pinProp) &&
            pinProp.GetString() == KioskSettings.WebAdminPin)
        {
            _token = Guid.NewGuid().ToString("N");
            await SendJson(res, new { token = _token });
        }
        else
        {
            await SendJson(res, new { error = "Invalid PIN" }, 401);
        }
    }

    //Users API

    private static async Task ApiGetUsers(HttpListenerResponse res)
    {
        var users = UserService.GetAll().Select(u => new
        {
            name          = u.Name,
            allowed_racks = u.AllowedRacks
        });
        await SendJson(res, users);
    }

    private static async Task ApiSaveUsers(HttpListenerRequest req, HttpListenerResponse res)
    {
        var body = await ReadJsonAsync(req);
        var list = new List<object>();

        foreach (var item in body.EnumerateArray())
        {
            string name = item.GetProperty("name").GetString() ?? "";
            var racks   = item.GetProperty("allowed_racks")
                              .EnumerateArray()
                              .Select(r => r.GetString() ?? "")
                              .Where(r => !string.IsNullOrEmpty(r))
                              .ToList();
            list.Add(new { name, allowed_racks = racks });
        }

        var opts = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(list, opts);
        Directory.CreateDirectory(Path.GetDirectoryName(DataFiles.UsersJson)!);
        await File.WriteAllTextAsync(DataFiles.UsersJson, json);
        UserService.Reload();

        await SendJson(res, new { ok = true });
    }

    private static async Task ApiGetRacks(HttpListenerResponse res)
    {
        var racks = RackCatalogue.All.Select(r => new
        {
            tag         = r.Tag,
            displayName = r.DisplayName,
            row         = r.Row
        });
        await SendJson(res, racks);
    }

    //Photos API
    private static async Task ApiListPhotos(HttpListenerResponse res, string dir)
    {
        if (!Directory.Exists(dir))
        { await SendJson(res, Array.Empty<object>()); return; }

        var photos = Directory.GetFiles(dir)
            .Where(f => { var e = Path.GetExtension(f).ToLower(); return e is ".jpg" or ".jpeg" or ".png"; })
            .OrderBy(f => f)
            .Select(f =>
            {
                var stem    = Path.GetFileNameWithoutExtension(f);
                var display = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(stem.Replace("_", " "));
                return new { fileName = Path.GetFileName(f), displayName = display };
            });

        await SendJson(res, photos);
    }

    private static async Task ApiUploadPhoto(
        HttpListenerRequest req, HttpListenerResponse res, string dir)
    {
        var body = await ReadJsonAsync(req);
        string name = body.GetProperty("name").GetString()?.Trim() ?? "";
        string ext  = body.GetProperty("ext").GetString()?.TrimStart('.').ToLower() ?? "jpg";
        string b64  = body.GetProperty("data").GetString() ?? "";

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(b64))
        { await SendJson(res, new { error = "name and data required" }, 400); return; }

        if (ext != "jpg" && ext != "jpeg" && ext != "png") ext = "jpg";

        byte[] bytes = Convert.FromBase64String(b64);
        Directory.CreateDirectory(dir);

        string stem = name.ToLowerInvariant().Replace(" ", "_");
        string dest = Path.Combine(dir, $"{stem}.{ext}");
        await File.WriteAllBytesAsync(dest, bytes);

        await SendJson(res, new { ok = true, fileName = Path.GetFileName(dest) });
    }

    private static async Task ApiDeletePhoto(
        HttpListenerRequest req, HttpListenerResponse res, string dir)
    {
        string? file = req.QueryString["file"];
        if (string.IsNullOrEmpty(file))
        { await SendJson(res, new { error = "file param required" }, 400); return; }

        string safe = Path.GetFileName(file);
        string path = Path.Combine(dir, safe);
        if (File.Exists(path)) File.Delete(path);

        await SendJson(res, new { ok = true });
    }

    //HTTP helpers 

    private static async Task<JsonElement> ReadJsonAsync(HttpListenerRequest req)
    {
        using var sr   = new StreamReader(req.InputStream, req.ContentEncoding);
        string    body = await sr.ReadToEndAsync();
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task SendJson(HttpListenerResponse res, object data, int status = 200)
    {
        res.StatusCode      = status;
        res.ContentType     = "application/json; charset=utf-8";
        byte[] bytes        = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    private static async Task SendHtml(HttpListenerResponse res, string html)
    {
        res.StatusCode      = 200;
        res.ContentType     = "text/html; charset=utf-8";
        byte[] bytes        = Encoding.UTF8.GetBytes(html);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  EMBEDDED ADMIN SPA
    // ═════════════════════════════════════════════════════════════════════════

    private static string AdminHtml() => """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1.0"/>
<title>ST Engineering — Kiosk Admin</title>
<style>
  *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
  :root {
    --red:     #C8102E;
    --red-dk:  #A00D23;
    --red-lt:  #FFF1F2;
    --gray0:   #F8F9FA;
    --gray1:   #F3F4F6;
    --gray2:   #E5E7EB;
    --gray3:   #9CA3AF;
    --gray4:   #6B7280;
    --gray5:   #374151;
    --black:   #111827;
    --green:   #16A34A;
    --green-lt:#F0FFF4;
  }
  body { font-family: 'Segoe UI', sans-serif; background: var(--gray0); color: var(--black); min-height: 100vh; }

  /* ── header ── */
  header {
    background: #fff; border-bottom: 1px solid var(--gray2);
    height: 56px; display: flex; align-items: center; padding: 0 24px;
    justify-content: space-between; position: sticky; top: 0; z-index: 10;
    box-shadow: 0 1px 4px rgba(0,0,0,.06);
  }
  .brand { display: flex; align-items: center; gap: 10px; }
  .brand-bar  { width: 4px; height: 32px; background: var(--red); border-radius: 3px; }
  .brand-logo { height: 38px; width: auto; }
  .brand-name { font-size: 15px; font-weight: 800; color: var(--red); }
  .brand-sub  { font-size: 11px; color: var(--gray4); margin-top: -2px; }
  #btn-logout { display: none; }

  /* ── login ── */
  #login-screen {
    display: flex; align-items: center; justify-content: center;
    min-height: calc(100vh - 56px);
  }
  .login-card {
    background: #fff; border: 1px solid var(--gray2); border-radius: 12px;
    padding: 40px 48px; width: 360px; text-align: center;
    box-shadow: 0 4px 24px rgba(0,0,0,.07);
  }
  .login-icon {
    width: 72px; height: 72px; border-radius: 50%;
    background: var(--red-lt); border: 2px solid var(--red);
    font-size: 32px; display: flex; align-items: center; justify-content: center;
    margin: 0 auto 20px;
  }
  .login-title { font-size: 20px; font-weight: 700; color: var(--black); margin-bottom: 6px; }
  .login-sub   { font-size: 13px; color: var(--gray4); margin-bottom: 28px; }
  .login-card input[type=password] {
    width: 100%; padding: 10px 14px; border: 1px solid var(--gray2);
    border-radius: 6px; font-size: 22px; letter-spacing: 6px;
    text-align: center; margin-bottom: 16px; outline: none;
    transition: border-color .15s;
  }
  .login-card input[type=password]:focus { border-color: var(--red); }
  #login-error { color: var(--red); font-size: 12px; margin-bottom: 12px; min-height: 16px; }

  /* ── app ── */
  #app { display: none; max-width: 1100px; margin: 0 auto; padding: 24px 20px; }

  /* ── tabs ── */
  .tabs { display: flex; gap: 4px; border-bottom: 2px solid var(--gray2); margin-bottom: 24px; }
  .tab-btn {
    padding: 10px 20px; background: none; border: none; cursor: pointer;
    font-size: 14px; font-weight: 600; color: var(--gray4);
    border-bottom: 2px solid transparent; margin-bottom: -2px; transition: color .15s;
  }
  .tab-btn.active { color: var(--red); border-bottom-color: var(--red); }
  .tab-panel { display: none; }
  .tab-panel.active { display: block; }

  /* ── card ── */
  .card { background: #fff; border: 1px solid var(--gray2); border-radius: 10px; overflow: hidden; }
  .card-header {
    padding: 14px 20px; border-bottom: 1px solid var(--gray2);
    font-size: 10px; font-family: Consolas, monospace; color: var(--gray4);
    display: flex; align-items: center; justify-content: space-between;
    background: var(--gray0);
  }
  .card-body { padding: 20px; }
  .two-col { display: grid; grid-template-columns: 240px 1fr; gap: 16px; }
  @media (max-width: 700px) { .two-col { grid-template-columns: 1fr; } }

  /* ── user list ── */
  .user-list { list-style: none; padding: 6px; }
  .user-item {
    padding: 10px 12px; cursor: pointer; border-radius: 6px;
    transition: background .12s; margin-bottom: 2px;
  }
  .user-item:hover    { background: var(--gray1); }
  .user-item.selected { background: var(--red-lt); border-left: 3px solid var(--red); }
  .user-item-name  { font-size: 13px; font-weight: 600; }
  .user-item-racks { font-size: 10px; color: var(--gray3); margin-top: 2px; }

  /* ── form fields ── */
  .field { margin-bottom: 16px; }
  .field label {
    display: block; font-size: 11px; font-weight: 600;
    color: var(--gray4); margin-bottom: 5px; letter-spacing: .03em;
  }
  .field input, .field textarea, .field select {
    width: 100%; padding: 8px 10px; border: 1px solid var(--gray2);
    border-radius: 5px; font-size: 13px; font-family: inherit; outline: none;
    resize: vertical; background: #fff; transition: border-color .15s;
    appearance: none; -webkit-appearance: none;
  }
  .field select {
    background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='12' height='8' viewBox='0 0 12 8'%3E%3Cpath fill='%236B7280' d='M1 1l5 5 5-5'/%3E%3C/svg%3E");
    background-repeat: no-repeat; background-position: right 10px center;
    padding-right: 30px; cursor: pointer;
  }
  .field input:focus, .field textarea:focus, .field select:focus { border-color: var(--red); }

  /* ── info box ── */
  .info-box { border-radius: 8px; padding: 14px 16px; margin-bottom: 16px; border: 1px solid; }
  .info-box.red  { background: var(--red-lt); border-color: #FCA5A5; }
  .info-box.blue { background: #F0F9FF;       border-color: #BAE6FD; }
  .info-box-label { font-size: 10px; font-family: Consolas,monospace; margin-bottom: 4px; font-weight: 700; }
  .info-box.red  .info-box-label { color: var(--red); }
  .info-box.blue .info-box-label { color: #0369A1; }
  .info-box p { font-size: 12px; color: var(--gray5); line-height: 1.5; }

  .hint-box {
    background: #FFF8F8; border: 1px solid #FED7D7; border-radius: 6px;
    padding: 12px; font-family: Consolas,monospace; font-size: 11px;
    color: var(--gray4); max-height: 140px; overflow-y: auto;
  }
  .hint-box-label { font-size: 10px; color: var(--red); margin-bottom: 6px; font-weight: 700; }

  /* ── upload row ── */
  .upload-row {
    background: #fff; border: 1px solid var(--gray2); border-radius: 8px;
    padding: 16px; margin-bottom: 14px;
  }
  .upload-row-inner { display: flex; gap: 12px; align-items: flex-end; }
  .upload-row .field { margin: 0; flex: 1; }
  .file-label { font-size: 11px; color: var(--gray4); margin-top: 5px; }

  /* ── photo list ── */
  .photo-grid { display: flex; flex-direction: column; gap: 6px; padding: 10px; }
  .photo-item {
    display: flex; align-items: center; justify-content: space-between;
    background: var(--gray0); border: 1px solid var(--gray2);
    border-radius: 6px; padding: 10px 14px; transition: background .12s;
  }
  .photo-item:hover { background: var(--gray1); }
  .photo-item-name { font-size: 13px; font-weight: 600; }
  .photo-item-file { font-size: 10px; color: var(--gray3); font-family: Consolas,monospace; margin-top: 2px; }

  /* ── buttons ── */
  .btn {
    padding: 8px 18px; border: none; border-radius: 5px; cursor: pointer;
    font-size: 13px; font-weight: 600; font-family: inherit; transition: background .12s;
    white-space: nowrap;
  }
  .btn-primary       { background: var(--red);  color: #fff; }
  .btn-primary:hover { background: var(--red-dk); }
  .btn-outline       { background: #fff; color: var(--red); border: 1px solid var(--red); }
  .btn-outline:hover { background: var(--red-lt); }
  .btn-danger        { background: #DC2626; color: #fff; }
  .btn-danger:hover  { background: #B91C1C; }
  .btn-sm            { padding: 5px 12px; font-size: 12px; }
  .btn:disabled      { opacity: 0.4; cursor: not-allowed; }
  .btn-row           { display: flex; gap: 8px; justify-content: flex-end; margin-top: 16px; }

  /* ── badge ── */
  .badge {
    display: inline-block; padding: 2px 8px; border-radius: 999px;
    font-size: 10px; font-weight: 700; background: var(--red-lt); color: var(--red);
    margin-left: 6px;
  }

  /* ── toast ── */
  #toast {
    position: fixed; bottom: 28px; left: 50%; transform: translateX(-50%);
    padding: 10px 24px; border-radius: 8px; font-size: 13px; font-weight: 600;
    color: #fff; opacity: 0; pointer-events: none; transition: opacity .25s;
    z-index: 999; box-shadow: 0 4px 16px rgba(0,0,0,.18);
  }
  #toast.show { opacity: 1; }
  #toast.ok   { background: var(--green); }
  #toast.err  { background: #DC2626; }

  /* ── empty ── */
  .empty { text-align: center; padding: 40px; color: var(--gray3); font-size: 13px; }
</style>
</head>
<body>

<header>
  <div class="brand">
    <div class="brand-bar"></div>
    <img src="data:image/png;base64,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" class="brand-logo" alt="ST Engineering"/>
    <div>
      <div class="brand-sub" style="margin-top:0">Kiosk Admin Portal</div>
    </div>
  </div>
  <button id="btn-logout" class="btn btn-outline btn-sm" onclick="logout()">Sign Out</button>
</header>

<!-- ── LOGIN ─────────────────────────────────────────────── -->
<div id="login-screen">
  <div class="login-card">
    <img src="data:image/png;base64,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" style="height:64px;width:auto;margin:0 auto 16px;display:block" alt="ST Engineering"/>
    <div class="login-title">Admin Access</div>
    <div class="login-sub">Enter the admin PIN to continue</div>
    <div id="login-error"></div>
    <input type="password" id="pin-input" placeholder="••••" maxlength="20"/>
    <button class="btn btn-primary" style="width:100%" onclick="doLogin()">Sign In</button>
  </div>
</div>

<!-- ── APP ───────────────────────────────────────────────── -->
<div id="app">

  <div class="tabs">
    <button class="tab-btn active" onclick="showTab('users',this)">Users</button>
    <button class="tab-btn"        onclick="showTab('escorts',this)">Escort Photos</button>
    <button class="tab-btn"        onclick="showTab('uphotos',this)">User Photos</button>
  </div>

  <!-- ══ USERS ════════════════════════════════════════════ -->
  <div id="tab-users" class="tab-panel active">
    <div class="two-col">

      <!-- User list -->
      <div class="card">
        <div class="card-header">
          REGISTERED USERS
          <span id="user-count" class="badge">0</span>
        </div>
        <ul class="user-list" id="user-list"></ul>
        <div style="padding:10px">
          <button class="btn btn-primary" style="width:100%" onclick="addUser()">＋ Add New User</button>
        </div>
      </div>

      <!-- Edit form -->
      <div class="card">
        <div class="card-header">EDIT USER</div>
        <div class="card-body">
          <div class="field">
            <label>FULL NAME</label>
            <input type="text" id="u-name" placeholder="e.g. John Smith"/>
          </div>
          <div class="field">
            <label>ALLOWED RACKS — one tag per line</label>
            <textarea id="u-racks" rows="7" placeholder="A01&#10;A02&#10;B01"></textarea>
          </div>
          <div class="hint-box">
            <div class="hint-box-label">AVAILABLE RACK TAGS</div>
            <div id="rack-hint"></div>
          </div>
          <div class="btn-row">
            <button class="btn btn-danger"  onclick="deleteUser()">Delete</button>
            <button class="btn btn-primary" onclick="saveUser()">Save Changes</button>
          </div>
        </div>
      </div>

    </div>
  </div>

  <!-- ══ ESCORT PHOTOS ════════════════════════════════════ -->
  <div id="tab-escorts" class="tab-panel">
    <div class="info-box red">
      <div class="info-box-label">HOW IT WORKS</div>
      <p>Upload a clear face photo for each escort. Select an existing escort from the dropdown or type a new name. The photo is saved using the name you provide.</p>
    </div>

    <div class="upload-row">
      <div class="upload-row-inner">
        <div class="field">
          <label>ESCORT NAME — select existing or type new</label>
          <input type="text" id="escort-name" placeholder="e.g. John Smith" list="escort-suggestions"/>
          <datalist id="escort-suggestions"></datalist>
          <div class="file-label" id="escort-file-name">No file chosen</div>
        </div>
        <div style="display:flex;gap:8px;flex-shrink:0;padding-bottom:22px">
          <input type="file" id="escort-file" accept=".jpg,.jpeg,.png" style="display:none"
                 onchange="onEscortFileChosen()"/>
          <button class="btn btn-outline" onclick="document.getElementById('escort-file').click()">Choose Photo</button>
          <button class="btn btn-primary" onclick="uploadPhoto('escort')">✓ Upload &amp; Enrol</button>
        </div>
      </div>
    </div>

    <div class="card">
      <div class="card-header">
        ENROLLED ESCORTS
        <button class="btn btn-outline btn-sm" onclick="loadEscortPhotos()">↺ Refresh</button>
      </div>
      <div class="photo-grid" id="escort-photo-list"></div>
    </div>
  </div>

  <!-- ══ USER PHOTOS ══════════════════════════════════════ -->
  <div id="tab-uphotos" class="tab-panel">
    <div class="info-box blue">
      <div class="info-box-label">HOW IT WORKS</div>
      <p>Select the user from the dropdown (populated from the Users tab), then choose their face photo and click Upload &amp; Enrol. The name must exactly match the Users tab.</p>
    </div>

    <div class="upload-row">
      <div class="upload-row-inner">
        <div class="field">
          <label>SELECT USER</label>
          <select id="user-photo-select">
            <option value="">— choose a user —</option>
          </select>
          <div class="file-label" id="user-file-name">No file chosen</div>
        </div>
        <div style="display:flex;gap:8px;flex-shrink:0;padding-bottom:22px">
          <input type="file" id="user-file" accept=".jpg,.jpeg,.png" style="display:none"
                 onchange="onUserFileChosen()"/>
          <button class="btn btn-outline" onclick="document.getElementById('user-file').click()">Choose Photo</button>
          <button class="btn btn-primary" onclick="uploadPhoto('user')">✓ Upload &amp; Enrol</button>
        </div>
      </div>
    </div>

    <div class="card">
      <div class="card-header">
        ENROLLED USERS
        <button class="btn btn-outline btn-sm" onclick="loadUserPhotos()">↺ Refresh</button>
      </div>
      <div class="photo-grid" id="user-photo-list"></div>
    </div>
  </div>

</div><!-- /#app -->

<div id="toast"></div>

<script>
// ── State ─────────────────────────────────────────────────────────────────────
let TOKEN   = null;
let users   = [];
let editIdx = -1;

// ── Auth ──────────────────────────────────────────────────────────────────────
async function doLogin() {
  const pin = document.getElementById('pin-input').value;
  try {
    const r = await fetch('/api/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pin })
    });
    const d = await r.json();
    if (d.token) {
      TOKEN = d.token;
      document.getElementById('login-screen').style.display = 'none';
      document.getElementById('app').style.display          = 'block';
      document.getElementById('btn-logout').style.display   = 'block';
      document.getElementById('login-error').textContent    = '';
      loadAll();
    } else {
      document.getElementById('login-error').textContent = '✕ Incorrect PIN';
    }
  } catch {
    document.getElementById('login-error').textContent = '✕ Server unreachable';
  }
}

function logout() {
  TOKEN = null;
  document.getElementById('login-screen').style.display = 'flex';
  document.getElementById('app').style.display          = 'none';
  document.getElementById('btn-logout').style.display   = 'none';
  document.getElementById('pin-input').value            = '';
}

document.getElementById('pin-input')
  .addEventListener('keydown', e => { if (e.key === 'Enter') doLogin(); });

// ── Tabs ──────────────────────────────────────────────────────────────────────
function showTab(name, btn) {
  document.querySelectorAll('.tab-panel').forEach(p => p.classList.remove('active'));
  document.querySelectorAll('.tab-btn'  ).forEach(b => b.classList.remove('active'));
  document.getElementById('tab-' + name).classList.add('active');
  btn.classList.add('active');
}

// ── Bootstrap ─────────────────────────────────────────────────────────────────
async function loadAll() {
  await Promise.all([ loadUsers(), loadRackHint(), loadEscortPhotos(), loadUserPhotos() ]);
}

// ── Users ─────────────────────────────────────────────────────────────────────
async function loadUsers() {
  users = await api('GET', '/api/users');
  renderUserList();
  clearEditForm();
  populateUserPhotoDropdown();
}

function renderUserList() {
  const ul = document.getElementById('user-list');
  document.getElementById('user-count').textContent = users.length;
  if (!users.length) {
    ul.innerHTML = '<li class="empty">No users registered</li>';
    return;
  }
  ul.innerHTML = users.map((u, i) => `
    <li class="user-item${editIdx === i ? ' selected' : ''}" onclick="selectUser(${i})">
      <div class="user-item-name">${esc(u.name)}</div>
      <div class="user-item-racks">${u.allowed_racks.length ? u.allowed_racks.join(', ') : '(no racks assigned)'}</div>
    </li>`).join('');
}

function selectUser(i) {
  editIdx = i;
  const u = users[i];
  document.getElementById('u-name' ).value = u.name;
  document.getElementById('u-racks').value = u.allowed_racks.join('\n');
  renderUserList();
}

function clearEditForm() {
  editIdx = -1;
  document.getElementById('u-name' ).value = '';
  document.getElementById('u-racks').value = '';
}

function addUser() {
  users.push({ name: 'New User', allowed_racks: [] });
  selectUser(users.length - 1);
  renderUserList();
  document.getElementById('u-name').focus();
  document.getElementById('u-name').select();
}

async function saveUser() {
  const name  = document.getElementById('u-name').value.trim();
  const racks = document.getElementById('u-racks').value
    .split('\n').map(s => s.trim().toUpperCase()).filter(Boolean);

  if (!name) { toast('Please enter a name', 'err'); return; }

  if (editIdx < 0) {
    users.push({ name, allowed_racks: racks });
    editIdx = users.length - 1;
  } else {
    users[editIdx] = { name, allowed_racks: racks };
  }

  await api('POST', '/api/users', users);
  await loadUsers();
  toast('Saved successfully ✓', 'ok');
}

async function deleteUser() {
  if (editIdx < 0) { toast('Select a user first', 'err'); return; }
  if (!confirm(`Delete user "${users[editIdx].name}"?`)) return;
  users.splice(editIdx, 1);
  await api('POST', '/api/users', users);
  await loadUsers();
  toast('User deleted', 'ok');
}

// ── Rack hint ─────────────────────────────────────────────────────────────────
async function loadRackHint() {
  const racks = await api('GET', '/api/racks');
  document.getElementById('rack-hint').innerHTML =
    racks.map(r => `<div><strong>${esc(r.tag)}</strong> — ${esc(r.displayName)} <span style="color:var(--gray3)">(${esc(r.row)})</span></div>`).join('');
}

// ── User photo dropdown ───────────────────────────────────────────────────────
function populateUserPhotoDropdown() {
  const sel = document.getElementById('user-photo-select');
  const cur = sel.value;
  sel.innerHTML = '<option value="">— choose a user —</option>' +
    users.map(u => `<option value="${esc(u.name)}">${esc(u.name)}</option>`).join('');
  if (cur) sel.value = cur;
}

// ── Escort photo datalist ─────────────────────────────────────────────────────
function populateEscortSuggestions(photos) {
  const dl = document.getElementById('escort-suggestions');
  dl.innerHTML = photos.map(p =>
    `<option value="${esc(p.displayName)}"></option>`).join('');
}

// ── File chosen handlers ──────────────────────────────────────────────────────
function onEscortFileChosen() {
  const file = document.getElementById('escort-file').files[0];
  if (!file) return;
  document.getElementById('escort-file-name').textContent = file.name;
  // Auto-fill name from filename only if field is empty
  const nameInput = document.getElementById('escort-name');
  if (!nameInput.value.trim()) {
    const stem = file.name.replace(/\.[^.]+$/, '').replace(/_/g, ' ');
    nameInput.value = stem.replace(/\b\w/g, c => c.toUpperCase());
  }
}

function onUserFileChosen() {
  const file = document.getElementById('user-file').files[0];
  if (!file) return;
  document.getElementById('user-file-name').textContent = file.name;
  // No auto-fill — user must select from dropdown
}

// ── Upload photo ──────────────────────────────────────────────────────────────
async function uploadPhoto(type) {
  let name, fileInput, fileLabelId, endpoint;

  if (type === 'escort') {
    name       = document.getElementById('escort-name').value.trim();
    fileInput  = document.getElementById('escort-file');
    fileLabelId= 'escort-file-name';
    endpoint   = 'escorts';
  } else {
    name       = document.getElementById('user-photo-select').value;
    fileInput  = document.getElementById('user-file');
    fileLabelId= 'user-file-name';
    endpoint   = 'users';
  }

  if (!name)                   { toast('Select or enter a name first', 'err'); return; }
  if (!fileInput.files.length) { toast('Choose a photo first', 'err');         return; }

  const file = fileInput.files[0];
  const ext  = file.name.split('.').pop().toLowerCase();
  const b64  = await toBase64(file);

  await api('POST', `/api/photos/${endpoint}`, { name, ext, data: b64 });

  // Reset inputs
  if (type === 'escort') {
    document.getElementById('escort-name').value = '';
  } else {
    document.getElementById('user-photo-select').value = '';
  }
  fileInput.value = '';
  document.getElementById(fileLabelId).textContent = 'No file chosen';

  toast(`${name} enrolled ✓  —  restart kiosk to apply`, 'ok');
  if (type === 'escort') loadEscortPhotos(); else loadUserPhotos();
}

// ── Photo lists ───────────────────────────────────────────────────────────────
async function loadEscortPhotos() {
  const photos = await api('GET', '/api/photos/escorts');
  renderPhotoList('escort-photo-list', photos, 'escorts');
  populateEscortSuggestions(photos);
}

async function loadUserPhotos() {
  const photos = await api('GET', '/api/photos/users');
  renderPhotoList('user-photo-list', photos, 'users');
}

function renderPhotoList(containerId, photos, endpoint) {
  const el = document.getElementById(containerId);
  if (!photos.length) {
    el.innerHTML = '<div class="empty">No photos enrolled yet</div>';
    return;
  }
  el.innerHTML = photos.map(p => `
    <div class="photo-item">
      <div>
        <div class="photo-item-name">${esc(p.displayName)}</div>
        <div class="photo-item-file">${esc(p.fileName)}</div>
      </div>
      <button class="btn btn-danger btn-sm"
              onclick="deletePhoto('${esc(endpoint)}','${esc(p.fileName)}',this)">🗑 Remove</button>
    </div>`).join('');
}

async function deletePhoto(endpoint, fileName, btn) {
  if (!confirm(`Delete photo "${fileName}"?`)) return;
  btn.disabled = true;
  await api('DELETE', `/api/photos/${endpoint}?file=${encodeURIComponent(fileName)}`);
  toast('Photo deleted', 'ok');
  if (endpoint === 'escorts') loadEscortPhotos(); else loadUserPhotos();
}

// ── API helper ────────────────────────────────────────────────────────────────
async function api(method, path, body) {
  const opts = { method, headers: { 'X-Token': TOKEN } };
  if (body !== undefined) {
    opts.headers['Content-Type'] = 'application/json';
    opts.body = JSON.stringify(body);
  }
  const r = await fetch(path, opts);
  if (!r.ok) {
    const e = await r.json().catch(() => ({ error: r.statusText }));
    throw new Error(e.error || r.statusText);
  }
  return r.json();
}

// ── Utils ─────────────────────────────────────────────────────────────────────
function toBase64(file) {
  return new Promise((res, rej) => {
    const r = new FileReader();
    r.onload  = () => res(r.result.split(',')[1]);
    r.onerror = () => rej(new Error('File read failed'));
    r.readAsDataURL(file);
  });
}

function esc(s) {
  return String(s)
    .replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;')
    .replace(/"/g,'&quot;').replace(/'/g,'&#39;');
}

let _toastTimer;
function toast(msg, type) {
  const el = document.getElementById('toast');
  el.textContent = msg;
  el.className   = `show ${type}`;
  clearTimeout(_toastTimer);
  _toastTimer = setTimeout(() => el.className = '', 3500);
}
</script>
</body>
</html>
""";
}