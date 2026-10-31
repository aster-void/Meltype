// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 aster-void

namespace Meltype.Config;

/// <summary>
/// config.json の設定。<see cref="Current"/> を読むたびに更新時刻を見て、変わっていれば読み直す
/// (Mac・Linux 版では、設定画面やエディターが入力とは別のプロセスで config.json を書く)。
/// </summary>
internal sealed class SettingsFile(string path, Action<Settings>? loaded = null)
{
    private Settings? _settings;
    private DateTime _writeTime;

    public Settings Current
    {
        get
        {
            // 読む前の時刻を覚える。読んでいる間に書かれても、次に読み直す (Load の移行で保存したときも 1 回読み直す)
            var writeTime = File.GetLastWriteTimeUtc(path);
            if (_settings is null || writeTime != _writeTime)
            {
                _writeTime = writeTime;
                _settings = Settings.Load(path);
                loaded?.Invoke(_settings);
            }
            return _settings;
        }
    }
}
