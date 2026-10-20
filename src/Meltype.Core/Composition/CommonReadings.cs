// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Eisenferik

using Meltype.Detection;

namespace Meltype.Composition;

/// <summary>
/// よく使う語の読み (dictionaries/readings.txt の「読み [TAB] 活用の種類」の読み)。
/// 読みの集合を一度だけ構築し、かな入力の判定とローマ字の打ち間違い直しで共有する。
/// </summary>
internal static class CommonReadings
{
    private static readonly Lazy<PackedWordSet> SharedWords = new(() => PackedWordSet.FromLines(ReadText(), firstField: true));

    /// <summary>読みの集合 (最初に使うときに読む)。</summary>
    public static PackedWordSet Words => SharedWords.Value;

    /// <summary>活用形の生成に使う本文を読み直す。共有するのは読みの集合だけで、本文は保持しない。</summary>
    public static string ReadText() => DictionarySource.ReadEmbedded("readings.txt");
}
