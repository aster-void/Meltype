// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using System.Net;
using System.Text;
using System.Text.Json;
using Meltype.Composition;
using Meltype.Config;

namespace Meltype.Tests;

/// <summary>Zenzai (llama-server の /completion) の変換。本物のサーバーの代わりに、同じ形で答える小さな HTTP サーバーを動かす。</summary>
internal static class ZenzaiConverterTests
{
    /// <summary>受け取った prompt を記録して、answer(prompt) を content にして返す。status が 200 以外ならそのエラーを返す。</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public List<string> Prompts { get; } = [];
        public int Status { get; set; } = 200;
        public Func<string, string> Answer { get; set; } = _ => "";

        /// <summary>答えの 1 文字ごとの確率 (本物のサーバーは completion_probabilities で返す)。</summary>
        public double Probability { get; set; } = 0.9;
        public Uri Endpoint { get; }

        public FakeServer()
        {
            var port = 0;
            for (var tries = 0; tries < 20; tries++)
            {
                port = Random.Shared.Next(20000, 60000);
                try { _listener.Prefixes.Add($"http://127.0.0.1:{port}/"); _listener.Start(); break; }
                catch (HttpListenerException) { _listener.Prefixes.Clear(); }
            }
            Endpoint = new Uri($"http://127.0.0.1:{port}/");
            Task.Run(Loop);
        }

        private void Loop()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = _listener.GetContext();
                    var body = new StreamReader(context.Request.InputStream, Encoding.UTF8).ReadToEnd();
                    var prompt = "";
                    if (body.Length > 0) prompt = JsonDocument.Parse(body).RootElement.GetProperty("prompt").GetString() ?? "";
                    lock (Prompts) Prompts.Add(prompt);
                    context.Response.StatusCode = Status;
                    var answer = Answer(prompt);
                    var probabilities = answer.Select(ch => new { content = ch.ToString(), probs = new[] { new { tok_str = ch.ToString(), prob = Probability } } }).ToArray();
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { content = answer, completion_probabilities = probabilities }));
                    context.Response.OutputStream.Write(bytes);
                    context.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            }
        }

        public void Dispose() => _listener.Close();
    }

    [Test]
    public static void Prompt_UsesKatakanaAndContext()
    {
        Assert.Equal("キョウ", ZenzaiConverter.Prompt("きょう", null), "文脈なし");
        Assert.Equal("アツイこの本は", ZenzaiConverter.Prompt("あつい", "この本は"), "文脈あり");
        Assert.Equal("ー、。ABC", ZenzaiConverter.ToKatakana("ー、。ABC"), "かな以外はそのまま");
    }

    [Test]
    public static void Clean_RemovesEndMarks()
    {
        Assert.Equal("今日", ZenzaiConverter.Clean("今日</s>")!, "EOS");
        Assert.Equal("今日", ZenzaiConverter.Clean(" 今日ゴミ")!, "区切り記号以降");
        Assert.True(ZenzaiConverter.Clean("</s>") is null, "空は null");
    }

    [Test]
    public static void Converts_ThroughCompletionEndpoint()
    {
        using var server = new FakeServer { Answer = p => p.Contains("キョウ") ? "今日</s>" : "" };
        using var zenzai = new ZenzaiConverter(server.Endpoint);
        var clauses = zenzai.ConvertClauses("きょう", "朝");
        Assert.True(clauses is [{ Reading: "きょう", Text: "今日" }], "1 つの文節で返る");
        Assert.True(server.Prompts.Count == 1 && server.Prompts[0] == "キョウ朝", "渡した文: " + string.Join("|", server.Prompts));
        zenzai.ConvertClauses("きょう", "朝");
        Assert.Equal(1, server.Prompts.Count, "同じ読みと文脈は覚えている");
    }

    [Test]
    public static void NoContext_UsesNeutralSentenceStart()
    {
        // 前の文脈が無い入力は、モデルが迷う (文脈なしの きょう → 強) ので、文の始まりの合図「。」を文脈として渡す
        using var server = new FakeServer { Answer = p => "今日" };
        using var zenzai = new ZenzaiConverter(server.Endpoint);
        Assert.True(zenzai.ConvertClauses("きょう") is [{ Text: "今日" }], "文脈なしでも Zenzai で変換する");
        Assert.True(zenzai.ConvertClauses("きょう", "") is [{ Text: "今日" }], "空の文脈も同じ");
        Assert.Equal("\uEE00キョウ\uEE02。\uEE01", server.Prompts[0], "渡した文: " + server.Prompts[0]);
        zenzai.ConvertClauses("あした", "昨日は");
        Assert.Equal("\uEE00アシタ\uEE02昨日は\uEE01", server.Prompts[^1], "文脈があればそれを渡す");
    }

    [Test]
    public static void UnchangedAnswer_IsNotUsed_AndDoesNotCountAsFailure()
    {
        using var server = new FakeServer { Answer = p => "かんたんなもんだい" };
        using var zenzai = new ZenzaiConverter(server.Endpoint);
        for (var i = 0; i < 4; i++) Assert.True(zenzai.ConvertClauses("かんたんなもんだい", "前の文") is null, "読みのままなら null");
        Assert.True(zenzai.IsAvailable, "変換しなかっただけでは諦めない");
    }

    [Test]
    public static void ServerErrors_FallBackAndGiveUp()
    {
        using var server = new FakeServer { Status = 500 };
        using var zenzai = new ZenzaiConverter(server.Endpoint);
        for (var i = 0; i < 3; i++) Assert.True(zenzai.ConvertClauses("あ" + new string('い', i), "前の文") is null, "失敗したら null");
        Assert.True(!zenzai.IsAvailable, "3 回失敗したら諦める");
        Assert.True(!new ZenzaiConverter("/no/such/llama-server", "/no/such.gguf").IsAvailable, "実行ファイルが無ければ使えない");
    }

    [Test]
    public static void SlowServer_PausesZenzaiForAWhile()
    {
        using var server = new FakeServer { Answer = p => { Thread.Sleep(300); return "遅い"; } };
        using var zenzai = new ZenzaiConverter(server.Endpoint, timeoutMs: 100);
        Assert.True(zenzai.ConvertClauses("あ", "前の文") is null, "間に合わなければ null");
        var before = server.Prompts.Count;
        Assert.True(zenzai.ConvertClauses("い", "前の文") is null, "しばらくは頼まない");
        Thread.Sleep(50);
        Assert.Equal(before, server.Prompts.Count, "遅いあいだはサーバーに頼まない");
        Assert.True(zenzai.IsAvailable, "遅いだけでは諦めない (また使える)");
    }

    [Test]
    public static void Hybrid_PrefersZenzaiAndKeepsOtherClausesWhenSame()
    {
        using var server = new FakeServer { Answer = p => p.Contains("タンイヲトル") ? "単位を取る" : "ひらがな" };
        using var zenzai = new ZenzaiConverter(server.Endpoint);
        var hybrid = new HybridConverter(() => ConversionEngine.System, null, new CompositionTests.FakeConverter(), null, () => zenzai);
        // FakeConverter は たんいをとる を 単位を|取る の 2 文節にする。同じ文字列なら、その区切りを使う
        var same = hybrid.ConvertClauses("たんいをとる");
        Assert.True(same is [{ Text: "単位を" }, { Text: "取る" }], "同じなら区切りを使う: " + string.Join("|", same?.Select(c => c.Text) ?? []));
        // 違えば Zenzai の結果
        var other = hybrid.ConvertClauses("たんい", "前の文");
        Assert.True(other is [{ Reading: "たんい", Text: "ひらがな" }], "違えば Zenzai: " + string.Join("|", other?.Select(c => c.Text) ?? []));
        // 使わない設定 (null) なら今までどおり
        var off = new HybridConverter(() => ConversionEngine.System, null, new CompositionTests.FakeConverter(), null, () => null);
        Assert.Equal("単位", off.Convert("たんい") ?? "", "OFF は今までどおり");
    }

    [Test]
    public static void Align_BuildsTextFromEachClauseCandidates()
    {
        IReadOnlyList<(ConversionClause, IReadOnlyList<string>)> clauses =
            [(new("きしゃが", "記者が"), ["記者が", "汽車が", "帰社が"]), (new("きた", "来た"), ["来た", "着た", "北"])];
        var aligned = HybridConverter.Align(clauses, "汽車が着た");
        Assert.True(aligned is [{ Reading: "きしゃが", Text: "汽車が" }, { Reading: "きた", Text: "着た" }], "文節ごとの候補でできる: " + Joined(aligned));
        Assert.True(HybridConverter.Align(clauses, "記者が北") is [{ Text: "記者が" }, { Text: "北" }], "1 番目の候補もそのまま使う");
        Assert.True(HybridConverter.Align(clauses, "汽車が着たよ") is null, "候補をつないでも作れない (余る)");
        Assert.True(HybridConverter.Align(clauses, "喫茶が来た") is null, "候補に無い字がある");
        // Mozc が候補を返さなかった文節は、読みのままの文字列になる (MozcConverter.ConvertWithCandidates)。それも候補として使う
        IReadOnlyList<(ConversionClause, IReadOnlyList<string>)> noCandidates = [(new("きしゃが", "記者が"), ["記者が"]), (new("きた", "きた"), [])];
        Assert.True(HybridConverter.Align(noCandidates, "記者がきた") is [{ Text: "記者が" }, { Text: "きた" }], "候補が無い文節は読みのまま: " + Joined(HybridConverter.Align(noCandidates, "記者がきた")));
        Assert.True(HybridConverter.Align(noCandidates, "記者が来た") is null, "候補が無い文節は、読みのまま以外にはしない");
        // 半角の数字を打った読みは、Mozc が全角の候補 (１５日に) を返す。半角にそろえて比べ、半角で返す (CompositionController.NormalizeHalfWidth と同じ)
        IReadOnlyList<(ConversionClause, IReadOnlyList<string>)> digits = [(new("15にちに", "１５日に"), ["１５日に", "十五日に"]), (new("きた", "来た"), ["来た", "着た"])];
        var halfWidth = HybridConverter.Align(digits, "15日に着た");
        Assert.True(halfWidth is [{ Text: "15日に" }, { Text: "着た" }], "半角の数字: " + Joined(halfWidth));
        Assert.True(HybridConverter.Align(digits, "１５日に着た") is [{ Text: "15日に" }, { Text: "着た" }], "全角で返ってきても半角にそろえる");
        // 前の文節で長い候補を先に選ぶと後ろが作れないときは、短い候補に戻って試す
        // (候補は仮のもの。本物の Mozc で、同じ文節の候補の一方が他方の頭と同じになることはまれ)
        IReadOnlyList<(ConversionClause, IReadOnlyList<string>)> overlap = [(new("あ", "AB"), ["AB", "A"]), (new("い", "BC"), ["BC"])];
        var shorter = HybridConverter.Align(overlap, "ABC");
        Assert.True(shorter is [{ Text: "A" }, { Text: "BC" }], "戻って試す: " + Joined(shorter));
    }

    [Test]
    public static void Hybrid_AlignsZenzaiToMozcClauses_AndLearningStillApplies()
    {
        // 偽の Mozc のヘルパーは、きしゃがきた を 記者が|来た の 2 文節で、文節ごとの候補も付けて返す
        var profile = Path.Combine(Path.GetTempPath(), MozcConverterTests.FakeHelperProfile);
        using var mozc = new MozcConverter(Environment.ProcessPath!, profile);
        // ほかの読み (文節ごとの候補を作るときの きしゃが・きた など) は読みのまま返す。空を返すと失敗と数えられ、3 回で Zenzai を使わなくなる
        using var server = new FakeServer
        {
            Answer = p => p.Contains("キシャガキタ") ? (p.Contains("外") ? "帰社が北よ" : "汽車が着た") : p.Contains("ニモツヲニカイニハコブ") ? "荷物を2階に運ぶ" : ReadingIn(p),
        };
        // 遅い PC でも時間切れ (5 秒 Zenzai を使わない) にならないよう、待つ時間を長くする
        using var zenzai = new ZenzaiConverter(server.Endpoint, timeoutMs: 5000);
        var hybrid = new HybridConverter(() => ConversionEngine.Mozc, mozc, new CompositionTests.FakeConverter(), null, () => zenzai);
        mozc.WarmUp();
        for (var i = 0; i < 200 && mozc.ConvertClauses("きしゃがきた") is null; i++) Thread.Sleep(50);
        Assert.True(mozc.ConvertClauses("きしゃがきた") is [_, _], "偽の Mozc のヘルパーが 10 秒で起動しなかった");

        // Zenzai の結果 (汽車が着た) は Mozc の 1 番目 (記者が来た) と違うが、文節ごとの候補でできるので、Mozc の文節で返す
        var aligned = hybrid.ConvertClauses("きしゃがきた", "前の文");
        Assert.True(aligned is [{ Reading: "きしゃが", Text: "汽車が" }, { Reading: "きた", Text: "着た" }], "Mozc の文節で返す: " + Joined(aligned));
        // 候補でできなければ、今までどおり読み全体で 1 つの文節
        var whole = hybrid.ConvertClauses("きしゃがきた", "外の文");
        Assert.True(whole is [{ Reading: "きしゃがきた", Text: "帰社が北よ" }], "できなければ 1 文節: " + Joined(whole));

        // 文節が残るので、選び直して覚えた文節 (きしゃが → 帰社が) は Zenzai の結果の上から効く。1 文節だと効かなかった
        var history = new ConversionHistory(null);
        history.Remember("きしゃが", "帰社が");
        var k = new CompositionTests.Keyboard(history: history, converter: hybrid);
        k.Type("kishagakita ");
        Assert.Equal("帰社が着た", k.Showing ?? "", "覚えた文節は覚えた語、ほかは Zenzai の結果");
        // 文節ごとの候補に足す「その読みだけの変換」は Zenzai に頼まない (頼むと文節の数だけ待たされる)
        string[] asked;
        lock (server.Prompts) asked = server.Prompts.Select(ReadingIn).ToArray();
        Assert.True(!asked.Contains("きしゃが") && !asked.Contains("きた"), "文節だけを Zenzai に頼まない: " + string.Join("|", asked));

        // 助詞だけの文節 (に) は、かな以外の候補 (2) で当てはめない。当てはめると、助詞をかなに戻す決まりで 荷物をに階に運ぶ になる
        var cargoClauses = hybrid.ConvertClauses("にもつをにかいにはこぶ", "前の文");
        Assert.True(cargoClauses is [{ Text: "荷物を2階に運ぶ" }], "助詞の文節を数字にして当てはめない: " + Joined(cargoClauses));
        var cargo = new CompositionTests.Keyboard(converter: hybrid);
        cargo.Type("nimotsuwonikainihakobu ");
        Assert.Equal("荷物を2階に運ぶ", cargo.Showing ?? "", "助詞の文節を数字にしない (1 つの文節のまま)");
    }

    /// <summary>zenz に渡した文 (U+EE00 + カタカナの読み + …) の読みを、ひらがなにして返す。</summary>
    private static string ReadingIn(string prompt)
    {
        var start = prompt.IndexOf((char)0xEE00) + 1;
        var end = prompt.IndexOfAny([(char)0xEE01, (char)0xEE02], start);
        return string.Concat(prompt[start..(end < 0 ? prompt.Length : end)].Select(c => c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c));
    }

    private static string Joined(IReadOnlyList<ConversionClause>? clauses) => clauses is null ? "(null)" : string.Join("|", clauses.Select(c => $"{c.Reading}→{c.Text}"));

    /// <summary>
    /// 本物の llama-server と zenz のモデルがあるときだけ動かす (環境変数 MELTYPE_ZENZAI_SERVER と MELTYPE_ZENZAI_MODEL)。
    /// zenz の前処理 (gpt2-small-japanese-char) は本家の llama.cpp に無いが、起動するときに gpt-2 に差し替えて読む (本家の b11719 と zenz-v3.1-small で確かめた)。
    /// </summary>
    [Test]
    public static void RealZenz_ConvertsWhenAvailable()
    {
        if (Environment.GetEnvironmentVariable("MELTYPE_ZENZAI_SERVER") is not { Length: > 0 } server ||
            Environment.GetEnvironmentVariable("MELTYPE_ZENZAI_MODEL") is not { Length: > 0 } model) return;
        using var zenzai = new ZenzaiConverter(server, model, timeoutMs: 5000);
        zenzai.WarmUp();
        // 起動を待つ (モデルを読み込むので数秒かかる)
        var clauses = (IReadOnlyList<ConversionClause>?)null;
        for (var i = 0; i < 100 && clauses is null; i++)
        {
            clauses = zenzai.ConvertClauses("きょうはいいてんきですね");
            if (clauses is null) Thread.Sleep(300);
        }
        Assert.Equal("今日はいい天気ですね", clauses is [var c] ? c.Text : "(変換できなかった)", "文を変換");
        var chopsticks = zenzai.ConvertClauses("はし", "ごはんをたべるための");
        var bridge = zenzai.ConvertClauses("はし", "みずうみにかかる");
        Assert.Equal("箸", chopsticks is [var a] ? a.Text : "", "文脈 (食べる) で 箸");
        Assert.Equal("橋", bridge is [var b] ? b.Text : "", "文脈 (湖にかかる) で 橋");
        // 文脈の無い きょう は、何も渡すと モデルが迷って 強 になる。「。」を文脈にして 今日 にする
        Assert.Equal("今日", zenzai.ConvertClauses("きょう") is [var bare] ? bare.Text : "", "文脈なしでも きょう は 今日 (「。」を文脈にする: 何も渡さないと 強)");
        Assert.Equal("今日", zenzai.ConvertClauses("きょう", "あしたは") is [var today] ? today.Text : "", "文脈 (あしたは) で 今日");
    }

    [Test]
    public static void StaleServerFromAKilledMeltype_IsEndedOnNextStart()
    {
        // Meltype が強制終了されて llama-server が残ったときは、次に起動するときに終わらせる (PID ファイルの PID と名前と起動した時刻が合うときだけ)
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sleep")) return;
        using var stale = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sleep", "120") { UseShellExecute = false })!;
        var pidFile = Path.Combine(Path.GetTempPath(), "meltype-zenzai-server.pid");
        File.WriteAllText(pidFile, $"{stale.Id}\nsleep\n{stale.StartTime.ToUniversalTime().Ticks}");
        var model = Path.GetTempFileName();
        try
        {
            using var zenzai = new ZenzaiConverter("/bin/sleep", model);
            zenzai.WarmUp();
            Assert.True(stale.WaitForExit(5000), "残っていたサーバーが終わる");
        }
        finally
        {
            File.Delete(model);
            if (!stale.HasExited) stale.Kill();
            File.Delete(pidFile);
        }
    }

    [Test]
    public static void ProcessWithReusedPid_IsNotEnded()
    {
        // PID が使い回されて、同じ名前の別のプロセス (ユーザーが自分で動かしている llama-server など) になっていたら終わらせない
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sleep")) return;
        using var other = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sleep", "120") { UseShellExecute = false })!;
        var pidFile = Path.Combine(Path.GetTempPath(), "meltype-zenzai-server.pid");
        File.WriteAllText(pidFile, $"{other.Id}\nsleep\n{other.StartTime.ToUniversalTime().AddMinutes(-5).Ticks}");
        var model = Path.GetTempFileName();
        try
        {
            using var zenzai = new ZenzaiConverter("/bin/sleep", model);
            zenzai.WarmUp();
            Assert.True(!other.WaitForExit(2000), "起動した時刻が違うプロセスは終わらせない");
        }
        finally
        {
            File.Delete(model);
            if (!other.HasExited) other.Kill();
            File.Delete(pidFile);
        }
    }
}
