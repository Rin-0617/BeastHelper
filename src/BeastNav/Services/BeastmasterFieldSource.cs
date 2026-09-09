using System.Numerics;
using BeastNav.Models;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace BeastNav.Services;

public static class BeastmasterFieldSource
{
    private const float MapSpan = 41f;
    private const float RawSpan = 2048f;
    private const float RawCenter = RawSpan / 2f;

    private static readonly FieldEntry[] Entries =
    [
        new(2, 4, 23.400f, 16.300f, 1, 2, "中央森林 / スクウィレル", ["スクウィレル"]),
        new(3, 15, 23.725f, 25.375f, 3, 4, "中央ラノシア / シープ", ["シープ"]),
        new(4, 15, 21.047f, 19.072f, 4, 6, "中央ラノシア / プギル", ["プギル"]),
        new(5, 7, 27.860f, 24.313f, 5, 9, "北部森林 / オポオポ", ["オポオポ"]),
        new(6, 16, 31.049f, 17.504f, 4, 9, "低地ラノシア / ドードー", ["ドードー"]),
        new(7, 20, 20.303f, 28.133f, 6, 8, "西ザナラーン / ラスティコブラン", ["ラスティコブラン"]),
        new(8, 4, 21.300f, 27.200f, 10, 12, "中央森林 / ダイアマイト", ["ダイアマイト"]),
        new(9, 15, 15.339f, 14.639f, 10, 13, "中央ラノシア / メガロクラブ", ["メガロクラブ"]),
        new(10, 21, 22.807f, 28.832f, 1, 4, "中央ザナラーン / ヒュージ・ホーネット", ["ヒュージ・ホーネット"]),
        new(11, 20, 16.314f, 16.300f, 13, 13, "西ザナラーン / バザード", ["バザード"]),
        new(12, 15, 21.550f, 17.112f, 5, 7, "中央ラノシア / タイニー・マンドラゴラ", ["タイニー・マンドラゴラ"]),
        new(13, 4, 19.100f, 27.400f, 14, 14, "中央森林 / ゲシュンペスト", ["ゲシュンペスト"]),
        new(14, 15, 20.378f, 19.576f, 4, 8, "中央ラノシア / プーク・ハッチリング", ["プーク・ハッチリング"]),
        new(15, 20, 15.938f, 16.487f, 13, 13, "西ザナラーン / シックシェル", ["シックシェル"]),
        new(16, 18, 22.475f, 22.250f, 16, 16, "西ラノシア / キラーマンティス", ["キラーマンティス"]),
        new(19, 16, 26.767f, 15.600f, 7, 7, "低地ラノシア / ケーブバット", ["ケーブバット"]),
        new(20, 4, 22.700f, 24.900f, 10, 10, "中央森林 / ローズレット", ["ローズレット"]),
        new(21, 18, 24.500f, 23.600f, 16, 16, "西ラノシア / ロズリトペリカン", ["ロズリトペリカン"]),
        new(22, 20, 27.200f, 24.787f, 3, 4, "西ザナラーン / カクター", ["カクター"]),
        new(23, 23, 23.156f, 12.533f, 29, 29, "南ザナラーン / サンドストーン・ゴーレム", ["サンドストーン・ゴーレム"]),
        new(24, 17, 29.162f, 35.713f, 30, 30, "東ラノシア / アプカル", ["アプカル"]),
        new(25, 21, 21.171f, 23.882f, 12, 17, "中央ザナラーン / ジャイアントトータス", ["ジャイアントトータス"]),
        new(26, 15, 18.775f, 17.225f, 8, 8, "中央ラノシア / ウーンデッド・オーロックス", ["ウーンデッド・オーロックス"]),
        new(27, 20, 16.930f, 14.580f, 14, 14, "西ザナラーン / スカフィテ", ["スカフィテ"]),
        new(28, 23, 18.656f, 34.859f, 31, 32, "南ザナラーン / サンドウォーム", ["サンドウォーム"]),
        new(29, 21, 17.480f, 23.500f, 7, 7, "中央ザナラーン / スプリガン・グレイブラバー", ["スプリガン・グレイブラバー"]),
        new(30, 16, 27.900f, 20.638f, 12, 17, "低地ラノシア / モスレスグゥーブー", ["モスレスグゥーブー"]),
        new(31, 16, 24.500f, 22.500f, 4, 4, "低地ラノシア / リバートード", ["リバートード"]),
        new(32, 17, 30.795f, 24.895f, 33, 33, "東ラノシア / コリブリ", ["コリブリ"]),
        new(33, 30, 14.400f, 15.100f, 34, 34, "外地ラノシア / クァール / 天然要害 サスタシャ浸食洞", ["クァール", "チョッパー"]),
        new(34, 4, 30.900f, 20.300f, 9, 9, "中央森林 / アノール", ["アノール"]),
        new(35, 23, 24.808f, 38.342f, 32, 32, "南ザナラーン / ドレイク", ["ドレイク"]),
        new(36, 4, 22.900f, 17.100f, 12, 12, "中央森林 / トレント・サップリング", ["トレント・サップリング"]),
        new(39, 4, 14.000f, 21.900f, 31, 31, "中央森林 / ストローパー", ["ストローパー"]),
        new(40, 15, 20.471f, 18.871f, 7, 7, "中央ラノシア / ボギー", ["ボギー"]),
        new(41, 4, 26.500f, 18.000f, 6, 6, "中央森林 / ブラックエフト", ["ブラックエフト"]),
        new(42, 25, 26.500f, 12.956f, 45, 45, "モードゥナ / レイクコブラ", ["レイクコブラ"]),
    ];

    private static readonly DetailEntry[] Details =
    [
        new(1, "ジョブクエスト", []),
        new(17, "封鎖坑道 カッパーベル銅山", ["イコラウス・アイル"]),
        new(18, "魔獣領域 ハラタリ修練所", ["ドクトル"]),
        new(37, "流砂迷宮 カッターズクライ", ["ミュルミドン・ソルジャー", "ミュルミドン・セントリー", "ミュルミドン・ガード", "ミュルミドン・マーシャル", "ミュルミドン・プリンセス"]),
        new(38, "流砂迷宮 カッターズクライ / ドルムキマイラ討伐戦", ["キマイラ", "ドルムキマイラ"]),
        new(43, "ハイドラ討伐戦", ["ハイドラ"]),
        new(44, "腐敗遺跡 古アムダプール市街", ["ガッドフライ"]),
        new(45, "腐敗遺跡 古アムダプール市街", ["ロッティング・グルマン"]),
        new(46, "怪鳥巨塔 シリウス大灯台 / FATE: 黒い鳥 (アバラシア雲海)", ["ズー", "ズー・クックル", "マザーアンズー", "アンズー・プレット"]),
        new(47, "氷結潜窟 スノークローク大氷壁 / FATE: コマンダー！ (クルザス西部高地)", ["ワンディル", "アイスコマンダー"]),
        new(48, "逆襲要害 サスタシャ浸食洞 (Hard)", ["カーラボス"]),
        new(49, "大迷宮バハムート：侵攻編1", ["ラフレシア"]),
        new(50, "クリスタルタワー：古代の民の迷宮", ["キングベヒーモス"]),
    ];

    public static IReadOnlyList<BeastDestination> CreateDestinations(IDataManager dataManager, IReadOnlyList<BeastPet> pets)
    {
        var mapSheet = dataManager.GetExcelSheet<Map>();
        if (mapSheet is null)
        {
            return [];
        }

        var petByXbmRow = pets.ToDictionary(static pet => pet.XbmRowId, static pet => pet);
        var results = new List<BeastDestination>();

        foreach (var entry in Entries)
        {
            if (!petByXbmRow.TryGetValue(entry.XbmRowId, out var pet))
            {
                continue;
            }

            var map = mapSheet.FirstOrDefault(row => row.RowId == entry.MapRowId);
            if (map.RowId == 0)
            {
                continue;
            }

            var position = MapToWorld(entry.MapX, entry.MapY, map.SizeFactor, map.OffsetX, map.OffsetY);

            results.Add(new BeastDestination
            {
                XbmRowId = entry.XbmRowId,
                PetRowId = pet.PetRowId,
                TerritoryId = map.TerritoryType.RowId,
                MapId = entry.MapRowId,
                MapX = entry.MapX,
                MapY = entry.MapY,
                X = position.X,
                Y = position.Y,
                Z = position.Z,
                Radius = 7.5f,
                EnemyLevelMin = entry.EnemyLevelMin,
                EnemyLevelMax = entry.EnemyLevelMax,
                TargetNames = entry.TargetNames.ToList(),
                Label = $"{entry.Label} X{MapCoord(entry.MapX)} Y{MapCoord(entry.MapY)}",
                Source = "built-in",
                Approximate = true,
            });
        }

        var knownRows = results.Select(static destination => destination.XbmRowId).ToHashSet();
        foreach (var detail in Details)
        {
            if (!knownRows.Add(detail.XbmRowId) || !petByXbmRow.ContainsKey(detail.XbmRowId))
            {
                continue;
            }

            var pet = petByXbmRow[detail.XbmRowId];
            results.Add(new BeastDestination
            {
                XbmRowId = detail.XbmRowId,
                PetRowId = pet.PetRowId,
                Label = detail.Label,
                TargetNames = detail.TargetNames.ToList(),
                Source = "built-in",
                Approximate = true,
            });
        }

        return results;
    }

    public static Vector3 MapToWorld(float mapX, float mapY, uint scale, int offsetX, int offsetY)
    {
        var scaleFactor = scale / 100f;
        var rawX = ((mapX - 1f) / MapSpan * RawSpan) - RawCenter;
        var rawZ = ((mapY - 1f) / MapSpan * RawSpan) - RawCenter;
        return new Vector3((rawX / scaleFactor) - offsetX, 0f, (rawZ / scaleFactor) - offsetY);
    }

    private static string MapCoord(float value)
        => value.ToString("0.0");

    private sealed record FieldEntry(
        uint XbmRowId,
        uint MapRowId,
        float MapX,
        float MapY,
        ushort EnemyLevelMin,
        ushort EnemyLevelMax,
        string Label,
        string[] TargetNames);

    private sealed record DetailEntry(uint XbmRowId, string Label, string[] TargetNames);
}
