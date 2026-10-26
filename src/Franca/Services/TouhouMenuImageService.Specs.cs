namespace Franca.Services;

// 菜单精灵图布局表（矩形全部取自 thanm -l 的 sprite 表）与 zh-CN 人工校译覆盖表。
// 每图: Edits=清除/修补区域, Draws=装饰+文字（Txt.Literal=false 的 Source 进翻译管线）。
public static partial class TouhouMenuImageService
{
    internal static List<ImgSpec>? Specs(string gameId) => gameId switch
    {
        "th11" => new List<ImgSpec>
        {
            Title01Th11(), RankTh11(), SelectTh11(), ResultTh11(), WeaponTh11(),
            SlPlB("title/sl_pl00b.png", "楽園の巫女", "博麗霊夢", new[]
                { "妖怪に好かれやすい巫女", "本人は乗り気じゃなくても", "妖怪はノリノリ" }, dark: true),
            SlPlB("title/sl_pl01b.png", "普通の魔法使い", "霧雨魔理沙", new[]
                { "変装奴に好かれやすい人間", "温泉とお酒さえあれば", "テンションが上がる" }, dark: true),
            WeaponDetail("title/weapon_00a.png", new (string, string)[]
            {
                ("オプション", "ローリングオプション"),
                ("ショット", "「頂門紫針」"),
                ("", "真っ直ぐ飛ぶ紫色の針"),
                ("遠隔霊撃", "「非常識の裏側」"),
                ("", "一定時間かすり以外のあたり判定を失う"),
                ("支援特技", "画面端から反対側の画面端を攻撃できる"),
                ("", "画面端から反対の画面端へ瞬間移動できる"),
                ("", "操作：ショットを撃たずに画面端で方向キーを二回押す"),
            }),
            WeaponDetail("title/weapon_00b.png", new (string, string)[]
            {
                ("オプション", "左右配置オプション"),
                ("ショット", "「鬼神燐火術」"),
                ("", "ほんの少しホーミングする高威力貫通弾"),
                ("遠隔霊撃", "「萃霊花」"),
                ("", "当った敵弾を全てホーミング弾に変えて攻撃する"),
                ("支援特技", "画面内の全てのアイテムを回収できる"),
                ("", "操作：全てのボタンを離す"),
            }),
            WeaponDetail("title/weapon_00c.png", new (string, string)[]
            {
                ("オプション", "方向指定オプション"),
                ("", "移動方向と逆方向に攻撃が出来る"),
                ("", "低速時に固定可能"),
                ("ショット", "「深山颪」"),
                ("遠隔霊撃", "「鴉の闇」"),
                ("", "一定時間自分の周りを攻撃する"),
                ("支援特技", "超高速移動が出来る"),
                ("", "操作：ショットボタンを離す"),
            }),
            WeaponDetail("title/weapon_01a.png", new (string, string)[]
            {
                ("オプション", "人形オプション"),
                ("", "高速移動だと集中、低速移動だと広範囲攻撃"),
                ("ショット", "「レインボーワイヤー」"),
                ("", "やや速度の遅いショット"),
                ("遠隔霊撃", "「リモートサクリファイス」"),
                ("", "人形を犠牲にしてやや強力なレーザーを撃つ"),
                ("支援特技", "オプションを８つまで付けられる"),
                ("", "霊撃を最大８回ストックできる"),
            }),
            WeaponDetail("title/weapon_01b.png", new (string, string)[]
            {
                ("オプション", "五種類の配置"),
                ("", "火符 前方集中配置　水符 前方広範囲配置"),
                ("", "木符 斜め攻撃配置　金符 左右攻撃配置"),
                ("", "土符 後方攻撃配置"),
                ("ショット", "「エブリアングルショット」"),
                ("遠隔霊撃", "「ファイブシーズン」"),
                ("支援特技", "オプションの配置を切り替えられる"),
                ("", "操作：ショットと低速ボタンの同時押し"),
            }),
            WeaponDetail("title/weapon_01c.png", new (string, string)[]
            {
                ("オプション", "後方配置"),
                ("ショット", "「空中魚雷」"),
                ("", "後方から強力ミサイルを放つ"),
                ("遠隔霊撃", "「オプティカルカモフラージュ」"),
                ("", "一定時間バリアを張る。"),
                ("", "バリア中に被弾すると画面内の敵弾を全て消す。"),
                ("", "※最後まで被弾しないと少しパワーが回復します"),
                ("支援特技", "霊撃を使っても被弾しない限り霊撃を使用した事にならない"),
            }),
            PauseTh11(), FrontTh11(), LoadingTh(),
        }.Concat(StageLogosTh11()).ToList(),
        "th10" => new List<ImgSpec>
        {
            Title01Th10(), RankTh10(), SelectTh10(), ResultTh10(), WeaponTh10(),
            SlPlB("title/sl_pl00b.png", "楽園の巫女", "博麗霊夢", new[]
                { "暢気な巫女さんは", "割と広範囲攻撃を得意とする。" }, dark: false),
            SlPlB("title/sl_pl01b.png", "普通の魔法使い", "霧雨魔理沙", new[]
                { "陽気な魔法使いは", "速さと手癖の悪さでは右に出る者は少ない" }, dark: false),
            PauseTh10(), LoadingTh(),
        }.Concat(StageLogosTh10()).ToList(),
        _ => null,
    };

    // ---- title01.png 主菜单：常态/选中成对。th11 常态白字深红描边、选中红字；
    // th10 常态白字黑描边、选中深灰字白描边（配色采样自原图）----
    private static ImgSpec Title01Th11()
    {
        var edits = new List<Edit>();
        var draws = new List<object>();
        void Pair(int x, int y, string src)
        {
            edits.Add(new Edit(x, y, 128, 32, "clear"));
            edits.Add(new Edit(x + 128, y, 128, 32, "clear"));
            draws.Add(new Txt(x, y, 128, 32, src, 24, (255, 248, 246), (74, 16, 24), 1.6, true, true, 255, 0, true));
            draws.Add(new Txt(x + 128, y, 128, 32, src, 24, (192, 48, 64), (26, 4, 8), 1.6, true, true, 255, 0, true));
        }
        Pair(0, 0, "Game Start"); Pair(0, 32, "Extra Start"); Pair(0, 64, "Practice Start");
        Pair(0, 96, "Replay"); Pair(0, 128, "Player Data"); Pair(0, 160, "Music Room");
        Pair(0, 192, "Option"); Pair(0, 224, "Quit"); Pair(0, 256, "BGM Volume");
        Pair(0, 288, "SE Volume"); Pair(0, 320, "Default"); Pair(0, 352, "Key Config");
        Pair(256, 0, "Shot"); Pair(256, 32, "Special"); Pair(256, 64, "Slow");
        Pair(256, 96, "Pause"); Pair(256, 128, "Skip"); Pair(256, 160, "On"); Pair(256, 192, "Off");
        return new ImgSpec("title.anm", "title/title01.png", edits, draws); // 数字/% 行（sprite 42-63）不动
    }

    private static ImgSpec Title01Th10()
    {
        var edits = new List<Edit>();
        var draws = new List<object>();
        void Pair(int x, int y, int w, string src, int selOff = -1)
        {
            int sx = x + (selOff < 0 ? w : selOff);
            edits.Add(new Edit(x, y, w, 32, "clear"));
            edits.Add(new Edit(sx, y, w, 32, "clear"));
            draws.Add(new Txt(x, y, w, 32, src, 24, White, Black, 1.8, true, true, 255, 0, true));
            draws.Add(new Txt(sx, y, w, 32, src, 24, (40, 40, 40), White, 1.8, true, true, 255, 0, true));
        }
        void Single(int x, int y, int w, string src, bool sel)
        {
            edits.Add(new Edit(x, y, w, 32, "clear"));
            draws.Add(sel
                ? new Txt(x, y, w, 32, src, 24, (40, 40, 40), White, 1.8, true, true, 255, 0, true)
                : new Txt(x, y, w, 32, src, 24, White, Black, 1.8, true, true, 255, 0, true));
        }
        Pair(0, 0, 160, "Game Start"); Pair(0, 32, 160, "Extra Start"); Pair(0, 64, 208, "Practice Start");
        Pair(0, 96, 96, "Replay"); Pair(0, 128, 176, "Player Data"); Pair(0, 160, 176, "Music Room");
        Pair(0, 192, 176, "How to Play"); Pair(0, 224, 112, "Option"); Pair(0, 256, 80, "Quit");
        Pair(160, 256, 80, "Auto", 160); Pair(0, 288, 64, "Hint", 128); Pair(0, 320, 128, "Window");
        Pair(0, 352, 176, "BGM Volume"); Pair(0, 384, 144, "SE Volume"); Pair(0, 416, 112, "Default");
        Pair(0, 448, 160, "Key Config");
        Pair(320, 0, 80, "Shot"); Pair(320, 32, 96, "Special"); Pair(192, 96, 80, "Slow");
        Pair(352, 128, 80, "Skip"); Pair(356, 160, 64, "On"); Pair(356, 192, 64, "Off");
        Single(416, 64, 96, "Pause", true); Single(352, 96, 96, "Pause", false);
        edits.Add(new Edit(257, 289, 254, 30, "clear"));
        draws.Add(new Txt(257, 289, 254, 30, "Press Any Button", 22, White, Black, 1.8, true, true, 255, 0, true));
        return new ImgSpec("title.anm", "title/title01.png", edits, draws);
    }

    // ---- rank00.png 难度选择：th11 面板整格重绘（档名+描述+星级+背景英文艺术字）；
    // th10 保留英文大字与笔触，仅扩散修补并重绘两行描述 ----
    private static ImgSpec RankTh11()
    {
        var ranks = new (string word, (int, int, int) wc, string name, string s1, string s2, int stars)[]
        {
            ("EASY",    (100, 200, 150), "妖精級", "眠い時に遊ぶとさらに眠くなる難易度です。", "馬鹿にされないように頑張ろう。", 1),
            ("NORMAL",  (100, 140, 210), "河童級", "人間にとって親しみやすい難易度です。",     "でも油断するとアレを持って行かれますよ。", 2),
            ("HARD",    (220, 130, 80),  "天狗級", "本気を出してくる難易度です。",             "しっかりとパターンを組む必要があります。", 4),
            ("LUNATIC", (180, 110, 210), "鬼神級", "巫山戯た難易度です。",                     "お酒を呑みながらどうぞ。", 6),
            ("EXTRA",   (230, 180, 80),  "偶像級", "特別な難易度です。",                       "遊んでも遊ばなくても。", -1),
        };
        var edits = new List<Edit>();
        var draws = new List<object>();
        for (int col = 0; col < 2; col++) // 右列=灰色未解锁变体
        {
            bool gray = col == 1;
            int x0 = col * 256;
            for (int i = 0; i < 5; i++)
            {
                var r = ranks[i];
                int y0 = i * 96;
                edits.Add(new Edit(x0, y0, 256, 96, "clear"));
                draws.Add(new Txt(x0 + 16, y0 + 18, 224, 60, r.word, 52,
                    gray ? (150, 150, 150) : r.wc, null, 0, true, true, 90, -6, true, Literal: true));
                draws.Add(new Txt(x0 + 6, y0 + 0, 150, 36, r.name, 25,
                    gray ? (120, 120, 120) : (15, 15, 15), gray ? (235, 235, 235) : (250, 250, 250), 1.2));
                draws.Add(new Txt(x0 + 96, y0 + 4, 152, 26, r.stars < 0 ? "??????" : new string('★', r.stars), 15,
                    gray ? (140, 140, 140) : (235, 45, 45), (80, 10, 10), 1.0, true, true, 255, 0, false, Literal: true));
                draws.Add(new Txt(x0 + 6, y0 + 38, 246, 18, r.s1, 12.5, gray ? (125, 125, 125) : (20, 20, 20)));
                draws.Add(new Txt(x0 + 6, y0 + 58, 246, 18, r.s2, 12.5, gray ? (125, 125, 125) : (20, 20, 20)));
            }
        }
        return new ImgSpec("title.anm", "title/rank00.png", edits, draws);
    }

    private static ImgSpec RankTh10()
    {
        var descs = new (string s1, string s2)[]
        {
            ("操作に慣れる為の難易度です。", "ステップアップは人間の基本。"),
            ("ゲームを楽しむ為の難易度です。", "自分に自信を持つと幸せ。"),
            ("割と自慢できる難易度です。", "演出を愉しむ余裕など要らないね。"),
            ("いつもの難易度です。", "意味が分からなければ遊ばない。"),
            ("おや？遊ぶのかい？", "あんたも相当物好きだねえ。"),
        };
        var edits = new List<Edit>();
        var draws = new List<object>();
        for (int col = 0; col < 2; col++)
            for (int i = 0; i < 5; i++)
            {
                int x0 = col * 256, y0 = i * 96;
                edits.Add(new Edit(x0 + 2, y0 + 42, 252, 38, "dark"));
                draws.Add(new Txt(x0 + 8, y0 + 44, 244, 16, descs[i].s1, 12, Black));
                draws.Add(new Txt(x0 + 8, y0 + 62, 244, 16, descs[i].s2, 12, Black));
            }
        return new ImgSpec("title.anm", "title/rank00.png", edits, draws);
    }

    // ---- select01.png 画面标题：9 项 256x48，大字（英文花体→目标语言）+ 小字（日文副标题）----
    private static ImgSpec SelectTh11() => SelectTh(new (string big, string sub)[]
    {
        ("Rank Select", "難易度を選べ"), ("Player Select", "人間を選べ"), ("Weapon Select", "支援妖怪を選べ"),
        ("Replay", "リプレイを見たい"), ("Player Data", "プレイデータを見たい"), ("Music Room", "ちょっとお茶でも"),
        ("Name Regist", "君の名は"), ("Save Replay", "リプレイを保存したい"), ("Stage Select", "ステージを選べ"),
    }, (125, 125, 215));

    private static ImgSpec SelectTh10() => SelectTh(new (string big, string sub)[]
    {
        ("Rank Select", "難易度を選択"), ("Player Select", "人間を選択"), ("Weapon Select", "使用武器を選択"),
        ("Replay", "リプレイを見ます"), ("Player Data", "プレイデータを見ます"), ("Music Room", "神々の休憩所"),
        ("Name Regist", "名前を刻もう"), ("Save Replay", "リプレイを記録しよう"), ("Stage Select", "ステージを選択"),
    }, (235, 125, 145));

    private static ImgSpec SelectTh((string big, string sub)[] items, (int, int, int) bigFill)
    {
        var edits = new List<Edit>();
        var draws = new List<object>();
        for (int i = 0; i < 9; i++)
        {
            int y0 = i * 48;
            edits.Add(new Edit(0, y0, 256, 48, "clear"));
            draws.Add(new Txt(4, y0 + 0, 248, 30, items[i].big, 23, bigFill, White, 1.6, true, true, 255, 0, true));
            draws.Add(new Txt(110, y0 + 28, 142, 17, items[i].sub, 12.5, White, Black, 1.2, true, true));
        }
        return new ImgSpec("title.anm", "title/select01.png", edits, draws); // sprite 84/88 箭头图标不动
    }

    // ---- result00.png 结算界面：角色/难度/统计标签 128x16 网格 + 底部红字提示 ----
    private static ImgSpec ResultTh11() => Result(new[]
    {
        "博麗霊夢", "霧雨魔理沙", "八雲紫", "伊吹萃香", "射命丸文", "アリス", "パチュリー", "河城にとり",
        "イージー", "ノーマル", "ハード", "ルナティック", "エキストラ",
    });

    private static ImgSpec ResultTh10() => Result(new[]
    {
        "博麗霊夢", "霧雨魔理沙", "誘導装備", "前方集中装備", "封印装備", "貫通装備", "高威力装備", "魔法使い装備",
        "イージー", "ノーマル", "ハード", "ルナティック", "エキストラ",
    });

    private static ImgSpec Result(string[] cells)
    {
        var edits = new List<Edit>();
        var draws = new List<object>();
        for (int i = 0; i < cells.Length; i++)
        {
            int x = (i % 2) * 128, y = (i / 2) * 16;
            edits.Add(new Edit(x, y, 128, 16, "clear"));
            draws.Add(new Txt(x + 4, y, 120, 16, cells[i], 13, White, Black, 1.3, false, true, 255, 0, true));
        }
        foreach (var (x, y, t) in new[] { (128, 96, "総プレイ回数"), (0, 112, "プレイ時間"), (128, 112, "クリア回数") })
        {
            edits.Add(new Edit(x, y, 96, 16, "clear"));
            draws.Add(new Txt(x + 4, y, 90, 16, t, 13, White, Black, 1.3, false, true));
        }
        edits.Add(new Edit(0, 144, 256, 16, "clear"));
        draws.Add(new Txt(4, 144, 250, 16, "決定ボタンでスコア／スペルカードが切り替え", 12,
            (235, 70, 45), (70, 5, 5), 1.0, false, true));
        return new ImgSpec("title.anm", "title/result00.png", edits, draws);
    }

    // ---- weapon.png 装备/支援选择：th11 支援妖怪 6 项 x 2 列 + クリア済木牌；
    // th10 装备名 6 项 x 2 列 + 6 个射击/子机明细块（含红色笔触）+ 涂鸦 stamp ----
    private static ImgSpec WeaponTh11()
    {
        var names = new[]
        {
            "八雲紫（前方集中型）", "伊吹萃香（貫通誘導型）", "射命丸文（移動特化型）",
            "アリス・マーガトロイド（支援強化型）", "パチュリー・ノーレッジ（配置変化型）", "河城にとり（防御重視型）",
        };
        var edits = new List<Edit>();
        var draws = new List<object>();
        for (int col = 0; col < 2; col++)
            for (int i = 0; i < 6; i++)
            {
                int x0 = col * 256, y0 = i * 32;
                edits.Add(new Edit(x0, y0, 256, 32, "clear"));
                draws.Add(new Txt(x0 + 4, y0, 248, 32, names[i], 15,
                    col == 0 ? White : (145, 145, 145), Black, 1.3, false, true, 255, 0, true));
            }
        // 木牌 stamp（sprite 111）：红字修补保留木纹后重绘
        edits.Add(new Edit(10, 206, 110, 44, "red"));
        draws.Add(new Txt(12, 210, 106, 36, "クリア済", 20, (225, 50, 45), (120, 20, 15), 0.8, true, true, 255, -3, true));
        return new ImgSpec("title.anm", "title/weapon.png", edits, draws);
    }

    private static ImgSpec WeaponTh10()
    {
        var names = new[] { "誘導装備", "前方集中装備", "封印装備", "高威力装備", "貫通装備", "魔法使い装備" };
        var details = new (string shot, string opt)[]
        {
            ("ホーミングアミュレット", "後方配置"), ("封魔針", "前方配置"), ("妖怪バスター", "左右＆集中配置"),
            ("スプレッドスター", "トレース＆固定配置"), ("イリュージョンレーザー", "近接配置"), ("コールドインフェルノ", "前方＆絶対配置"),
        };
        var edits = new List<Edit>();
        var draws = new List<object>();
        for (int col = 0; col < 2; col++)
            for (int i = 0; i < 6; i++)
            {
                int x0 = col * 256, y0 = i * 42;
                edits.Add(new Edit(x0, y0, 256, 42, "clear"));
                draws.Add(new Txt(x0 + 4, y0, 248, 42, names[i], 22,
                    col == 0 ? White : (140, 140, 140), Black, 1.5, false, true, 255, 0, true));
            }
        for (int i = 0; i < 6; i++)
        {
            int y0 = 256 + i * 42;
            edits.Add(new Edit(0, y0, 352, 42, "clear"));
            draws.Add(new Ell(104, y0 + 3, 236, 16, (160, 40, 40), 170, -2));
            draws.Add(new Ell(104, y0 + 24, 236, 16, (160, 40, 40), 170, -2));
            draws.Add(new Txt(4, y0 + 1, 84, 19, "ショット：", 14, White, Black, 1.2));
            draws.Add(new Txt(88, y0 + 1, 258, 19, details[i].shot, 15, (240, 70, 55), (60, 5, 5), 1.2, false, true));
            draws.Add(new Txt(4, y0 + 22, 84, 19, "オプション：", 14, White, Black, 1.2));
            draws.Add(new Txt(88, y0 + 22, 258, 19, details[i].opt, 15, (240, 70, 55), (60, 5, 5), 1.2, false, true));
        }
        // 红色喷溅涂鸦 stamp（sprite 121）：暗色文字修补保留喷溅后重绘
        edits.Add(new Edit(352, 268, 128, 44, "dark"));
        draws.Add(new Txt(354, 274, 124, 30, "フルに遊び尽くせ", 17, (150, 40, 35), null, 0, true, true, 255, -6, true));
        return new ImgSpec("title.anm", "title/weapon.png", edits, draws);
    }

    // ---- 人物介绍卡（title.anm, 256x256 整图 sprite, 文字盖住立绘：清区+半透明底板）----
    private static ImgSpec SlPlB(string relPath, string title, string name, string[] desc, bool dark)
    {
        var plate = dark ? (0, 0, 0) : (255, 255, 255);
        byte alpha = dark ? (byte)130 : (byte)110;
        var titleFill = dark ? (235, 90, 90) : (200, 60, 60);
        var descFill = dark ? (240, 120, 120) : (150, 55, 30);
        var edits = new List<Edit>
        {
            new Edit(0, 0, 256, 32, "clear"),
            new Edit(0, 28, 256, 58, "clear"),
            new Edit(0, 108, 256, 148, "clear"),
        };
        var draws = new List<object>
        {
            new Plate(4, 2, 176, 28, alpha, plate),
            new Plate(6, 30, 196, 52, alpha, plate),
            new Plate(56, 144, 196, 110, alpha, plate),
            new Txt(10, 2, 164, 28, title, 18, titleFill, White, 1.2, false, true),
            new Txt(12, 30, 184, 52, name, 32, White, Black, 2.0, false, true, 255, 0, true),
        };
        for (int i = 0; i < desc.Length; i++)
            draws.Add(new Txt(62, 148 + i * 21, 186, 20, desc[i], 12.5,
                descFill, dark ? (40, 0, 0) : (255, 240, 220), 0.9, false, true, 255, 0, true));
        return new ImgSpec("title.anm", relPath, edits, draws);
    }

    // ---- 支援妖怪详情块（title.anm, 512x128 整图重绘, 8 行; 遠隔霊撃/支援特技标签紫色）----
    private static ImgSpec WeaponDetail(string relPath, (string label, string text)[] lines)
    {
        var edits = new List<Edit> { new Edit(0, 0, 512, 128, "clear") };
        var draws = new List<object>();
        const double step = 128.0 / 8;
        for (int i = 0; i < lines.Length; i++)
        {
            double y = i * step;
            if (lines[i].label.Length > 0)
            {
                bool accent = lines[i].label is "遠隔霊撃" or "支援特技";
                draws.Add(new Txt(4, y, 94, step, lines[i].label, 12.5,
                    accent ? (200, 175, 235) : White, Black, 1.1, false, true));
            }
            if (lines[i].text.Length > 0)
                draws.Add(new Txt(100, y, 408, step, lines[i].text, 12.5, White, Black, 1.1,
                    false, true, 255, 0, true));
        }
        return new ImgSpec("title.anm", relPath, edits, draws);
    }

    // ---- pause.png（front.anm, 256x256）：暂停/录像回放菜单。th11 十三格白字黑描边 ----
    private static ImgSpec PauseTh11()
    {
        var cells = new (int x, int y, int w, string src)[]
        {
            (48, 0, 68, "一時停止"), (116, 0, 68, "満身創痍"), (184, 0, 68, "再生終了"),
            (48, 32, 144, "一時停止を解除する"),
            (48, 64, 112, "タイトルへ戻る"), (160, 64, 64, "はい"),
            (48, 96, 128, "最初からやり直す"), (176, 96, 64, "いいえ"),
            (48, 128, 176, "リプレイを保存して終了"),
            (48, 160, 144, "本当にいいですか？"),
            (48, 192, 144, "リプレイを保存する"),
            (0, 224, 128, "もう一度再生する"), (128, 224, 128, "続けて再挑戦する"),
        };
        var edits = new List<Edit>();
        var draws = new List<object>();
        foreach (var (x, y, w, src) in cells)
        {
            edits.Add(new Edit(x, y, w, 32, "clear"));
            draws.Add(new Txt(x, y, w, 32, src, 18, White, Black, 1.3, true, true, 255, 0, true));
        }
        return new ImgSpec("front.anm", "ascii/pause.png", edits, draws); // sprite 62 左侧装饰条不动
    }

    // ---- pause.png（th10）：12 格 32px 行 ----
    private static ImgSpec PauseTh10()
    {
        var cells = new (int x, int y, int w, string src)[]
        {
            (0, 0, 128, "一時停止。"), (128, 0, 128, "次シーンへ"),
            (0, 32, 256, "一時停止を解除する"), (0, 64, 256, "タイトル画面へ戻る"),
            (0, 96, 256, "最初からやり直す"),
            (0, 128, 128, "満身創痍！"), (128, 128, 128, "継続する"),
            (0, 160, 256, "リプレイを保存する"),
            (0, 192, 96, "はい"), (96, 192, 96, "いいえ"),
            (0, 224, 128, "本当に？"), (128, 224, 128, "演目終了！"),
        };
        var edits = new List<Edit>();
        var draws = new List<object>();
        foreach (var (x, y, w, src) in cells)
        {
            edits.Add(new Edit(x, y, w, 32, "clear"));
            draws.Add(new Txt(x, y, w, 32, src, 18, White, Black, 1.3, true, true, 255, 0, true));
        }
        return new ImgSpec("front.anm", "ascii/pause.png", edits, draws);
    }

    // ---- front00.png（th11 front.anm）：结算界面 撃破時間/実時間 两格（HUD 英文部分不动）----
    private static ImgSpec FrontTh11() => new("front.anm", "front/front00.png",
        new List<Edit> { new Edit(336, 304, 64, 32, "clear") },
        new List<object>
        {
            new Txt(336, 304, 64, 16, "撃破時間", 11, White, Black, 0.9, true, true, 255, 0, true),
            new Txt(336, 320, 64, 16, "実時間", 11, White, Black, 0.9, true, true, 255, 0, true),
        });

    // ---- loading.png（ascii.anm, 128x128）：少女祈禱中（NowLoading 与图标不动）----
    private static ImgSpec LoadingTh() => new("ascii.anm", "ascii/loading.png",
        new List<Edit> { new Edit(1, 1, 126, 30, "clear") },
        new List<object> { new Txt(1, 1, 126, 30, "少女祈禱中", 18, (25, 25, 70), null, 0, true, true, 255, 0, true) });

    // ---- 关卡标题卡（st0Xlogo.anm）：译主题行与地名行，BGM 名单与美术字条不动 ----
    private static IEnumerable<ImgSpec> StageLogosTh11()
    {
        var stages = new (string anm, string rel, string prefix, string theme, string place)[]
        {
            ("st01logo.anm", "front/st01logo.png", "Stage 1　", "忘恩の地から吹く風", "幻想風穴"),
            ("st02logo.anm", "front/st02logo.png", "Stage 2　", "地上と過去を結ぶ深道", "地獄の深道"),
            ("st03logo.anm", "front/st03logo.png", "Stage 3　", "忘れられた雪の旧都", "旧地獄街道"),
            ("st04logo.anm", "front/st04logo.png", "Stage 4　", "誰からも好かれない恐怖の目", "地霊殿"),
            ("st05logo.anm", "front/st05logo.png", "Stage 5　", "昔時の業火", "灼熱地獄跡"),
            ("st06logo.anm", "front/st06logo.png", "Stage 6　", "荒々しき二つ目の太陽", "地底都市最深部"),
            ("st07logo.anm", "front/st07logo.png", "Extra Stage　", "地獄のラブリービジター", "守矢神社"),
        };
        foreach (var s in stages)
            yield return new ImgSpec(s.anm, s.rel,
                new List<Edit> { new Edit(0, 0, 384, 32, "clear"), new Edit(0, 32, 384, 16, "clear") },
                new List<object>
                {
                    new Txt(0, 0, 384, 32, s.theme, 21, White, Black, 1.4, true, true, 255, 0, true),
                    new Txt(0, 32, 376, 16, s.place, 11, White, Black, 1.0, false, true, 255, 0, true, Prefix: s.prefix),
                });
    }

    private static IEnumerable<ImgSpec> StageLogosTh10()
    {
        var stages = new (string anm, string rel, string place)[]
        {
            ("st01logo.anm", "front/st01logo.png", "妖怪の山の麓"),
            ("st02logo.anm", "front/st02logo.png", "妖怪の樹海"),
            ("st03logo.anm", "front/st03logo.png", "未踏の渓谷"),
            ("st04logo.anm", "front/st04logo.png", "九天の滝"),
            ("st05logo.anm", "front/st05logo.png", "守矢の神社"),
            ("st06logo.anm", "front/st06logo.png", "風神の湖"),
            ("st07logo.anm", "front/st07logo.png", "守矢の神社（本殿）"),
        };
        foreach (var s in stages)
            yield return new ImgSpec(s.anm, s.rel,
                new List<Edit> { new Edit(0, 488, 128, 24, "clear") },
                new List<object>
                {
                    new Txt(0, 488, 128, 24, s.place, 13, White, Black, 1.0, true, true, 255, 0, true),
                });
    }

    // ---- zh-CN 人工校译覆盖表：启用 zh-CN 时优先于端点管线；未命中条目仍走管线 ----
    internal static readonly IReadOnlyDictionary<string, string> CuratedZh =
        new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // title01/select01 英文词条
        ["Game Start"] = "开始游戏", ["Extra Start"] = "Extra 开始", ["Practice Start"] = "练习开始",
        ["Replay"] = "录像回放", ["Player Data"] = "游戏数据", ["Music Room"] = "音乐室",
        ["Option"] = "设置", ["Quit"] = "退出", ["BGM Volume"] = "BGM 音量", ["SE Volume"] = "音效音量",
        ["Default"] = "恢复默认", ["Key Config"] = "按键设置", ["Shot"] = "射击", ["Special"] = "符卡",
        ["Slow"] = "低速", ["Pause"] = "暂停", ["Skip"] = "跳过", ["On"] = "开", ["Off"] = "关",
        ["How to Play"] = "操作说明", ["Auto"] = "自动", ["Hint"] = "提示", ["Window"] = "窗口",
        ["Press Any Button"] = "按任意键",
        ["Rank Select"] = "难度选择", ["Player Select"] = "选择自机", ["Weapon Select"] = "选择武器",
        ["th11|Weapon Select"] = "选择支援",
        ["Name Regist"] = "登记名字", ["Save Replay"] = "保存录像", ["Stage Select"] = "选择关卡",
        // rank00 档名与描述
        ["妖精級"] = "妖精级", ["河童級"] = "河童级", ["天狗級"] = "天狗级",
        ["鬼神級"] = "鬼神级", ["偶像級"] = "偶像级",
        ["眠い時に遊ぶとさらに眠くなる難易度です。"] = "犯困的时候玩会变得更困的难度。",
        ["馬鹿にされないように頑張ろう。"] = "加油，别被人当笨蛋看哦。",
        ["人間にとって親しみやすい難易度です。"] = "适合人类的亲切难度。",
        ["でも油断するとアレを持って行かれますよ。"] = "但大意的话“那个”会被收走哦。",
        ["本気を出してくる難易度です。"] = "拿出真本事的难度。",
        ["しっかりとパターンを組む必要があります。"] = "需要好好规划弹幕路线。",
        ["巫山戯た難易度です。"] = "胡闹般的难度。",
        ["お酒を呑みながらどうぞ。"] = "不妨边喝酒边玩。",
        ["特別な難易度です。"] = "特别的难度。",
        ["遊んでも遊ばなくても。"] = "玩不玩随你。",
        ["操作に慣れる為の難易度です。"] = "熟悉操作用的难度。",
        ["ステップアップは人間の基本。"] = "循序渐进是人类的基本。",
        ["ゲームを楽しむ為の難易度です。"] = "享受游戏的难度。",
        ["自分に自信を持つと幸せ。"] = "相信自己就会幸福。",
        ["割と自慢できる難易度です。"] = "足以自夸的难度。",
        ["演出を愉しむ余裕など要らないね。"] = "可没有欣赏演出的余裕哦。",
        ["いつもの難易度です。"] = "一如既往的难度。",
        ["意味が分からなければ遊ばない。"] = "不明白含义的话就别碰。",
        ["おや？遊ぶのかい？"] = "哦？要玩吗？",
        ["あんたも相当物好きだねえ。"] = "你也是个相当好事的人呢。",
        // select01 日文副标题（th11 / th10）
        ["難易度を選べ"] = "选择难度", ["人間を選べ"] = "选择角色", ["支援妖怪を選べ"] = "选择支援妖怪",
        ["リプレイを見たい"] = "观看录像", ["プレイデータを見たい"] = "查看游戏数据",
        ["ちょっとお茶でも"] = "来杯茶吧", ["君の名は"] = "你的名字是",
        ["リプレイを保存したい"] = "保存录像", ["ステージを選べ"] = "选择关卡",
        ["難易度を選択"] = "选择难度", ["人間を選択"] = "选择角色", ["使用武器を選択"] = "选择使用的武器",
        ["リプレイを見ます"] = "观看录像", ["プレイデータを見ます"] = "查看游戏数据",
        ["神々の休憩所"] = "众神的休憩所", ["名前を刻もう"] = "刻下名字吧",
        ["リプレイを記録しよう"] = "记录录像吧", ["ステージを選択"] = "选择关卡",
        // result00 角色名/难度/统计标签
        ["博麗霊夢"] = "博丽灵梦", ["霧雨魔理沙"] = "雾雨魔理沙", ["八雲紫"] = "八云紫",
        ["伊吹萃香"] = "伊吹萃香", ["射命丸文"] = "射命丸文", ["アリス"] = "爱丽丝",
        ["パチュリー"] = "帕秋莉", ["河城にとり"] = "河城荷取",
        ["イージー"] = "简单", ["ノーマル"] = "普通", ["ハード"] = "困难",
        ["ルナティック"] = "疯狂", ["エキストラ"] = "Extra",
        ["総プレイ回数"] = "总游戏次数", ["プレイ時間"] = "游戏时间", ["クリア回数"] = "通关次数",
        ["決定ボタンでスコア／スペルカードが切り替え"] = "按确定键切换分数/符卡显示",
        // weapon（th11 支援妖怪 + 木牌）
        ["八雲紫（前方集中型）"] = "八云紫（前方集中型）",
        ["伊吹萃香（貫通誘導型）"] = "伊吹萃香（贯通诱导型）",
        ["射命丸文（移動特化型）"] = "射命丸文（移动特化型）",
        ["アリス・マーガトロイド（支援強化型）"] = "爱丽丝·玛格特罗依德（支援强化型）",
        ["パチュリー・ノーレッジ（配置変化型）"] = "帕秋莉·诺蕾姬（配置变化型）",
        ["河城にとり（防御重視型）"] = "河城荷取（防御重视型）",
        ["クリア済"] = "已通关",
        // weapon（th10 装备名 + 明细 + 涂鸦）
        ["誘導装備"] = "诱导装备", ["前方集中装備"] = "前方集中装备", ["封印装備"] = "封印装备",
        ["高威力装備"] = "高威力装备", ["貫通装備"] = "贯通装备", ["魔法使い装備"] = "魔法使装备",
        ["ショット："] = "射击：", ["オプション："] = "子机：",
        ["ホーミングアミュレット"] = "追踪护符", ["封魔針"] = "封魔针", ["妖怪バスター"] = "妖怪克星",
        ["スプレッドスター"] = "扩散之星", ["イリュージョンレーザー"] = "幻象激光",
        ["コールドインフェルノ"] = "寒冷地狱",
        ["後方配置"] = "后方配置", ["前方配置"] = "前方配置", ["左右＆集中配置"] = "左右＆集中配置",
        ["トレース＆固定配置"] = "追踪＆固定配置", ["近接配置"] = "近接配置", ["前方＆絶対配置"] = "前方＆绝对配置",
        ["フルに遊び尽くせ"] = "玩到尽兴",
        // 人物介绍卡（sl_pl00b/01b 共用两作）
        ["楽園の巫女"] = "乐园的巫女", ["普通の魔法使い"] = "普通的魔法使",
        ["妖怪に好かれやすい巫女"] = "容易被妖怪喜欢的巫女",
        ["本人は乗り気じゃなくても"] = "本人虽然兴致缺缺",
        ["妖怪はノリノリ"] = "妖怪们却劲头十足",
        ["変装奴に好かれやすい人間"] = "容易被乔装的家伙缠上的人类",
        ["温泉とお酒さえあれば"] = "只要有温泉和美酒",
        ["テンションが上がる"] = "情绪就会高涨",
        ["暢気な巫女さんは"] = "悠闲的巫女小姐",
        ["割と広範囲攻撃を得意とする。"] = "比较擅长广范围攻击。",
        ["陽気な魔法使いは"] = "开朗的魔法使",
        ["速さと手癖の悪さでは右に出る者は少ない"] = "论速度和手脚不干净，少有人能出其右。",
        // 支援妖怪详情（weapon_00a-01c）
        ["オプション"] = "子机", ["ショット"] = "射击", ["遠隔霊撃"] = "远隔灵击", ["支援特技"] = "支援特技",
        ["ローリングオプション"] = "滚动子机", ["左右配置オプション"] = "左右配置子机",
        ["方向指定オプション"] = "方向指定子机", ["人形オプション"] = "人偶子机", ["五種類の配置"] = "五种配置",
        ["「頂門紫針」"] = "「顶门紫针」", ["「鬼神燐火術」"] = "「鬼神燐火术」", ["「深山颪」"] = "「深山风」",
        ["「レインボーワイヤー」"] = "「彩虹钢丝」", ["「エブリアングルショット」"] = "「全角度射击」",
        ["「空中魚雷」"] = "「空中鱼雷」",
        ["「非常識の裏側」"] = "「非常识的里侧」", ["「萃霊花」"] = "「萃灵花」", ["「鴉の闇」"] = "「鸦之暗」",
        ["「リモートサクリファイス」"] = "「遥控献祭」", ["「ファイブシーズン」"] = "「五季」",
        ["「オプティカルカモフラージュ」"] = "「光学迷彩」",
        ["真っ直ぐ飛ぶ紫色の針"] = "笔直飞行的紫色之针",
        ["一定時間かすり以外のあたり判定を失う"] = "一定时间内失去除擦弹外的判定",
        ["画面端から反対側の画面端を攻撃できる"] = "可从画面一端攻击另一端",
        ["画面端から反対の画面端へ瞬間移動できる"] = "可从画面一端瞬间移动到另一端",
        ["操作：ショットを撃たずに画面端で方向キーを二回押す"] = "操作：不射击时在画面边缘连按两次方向键",
        ["ほんの少しホーミングする高威力貫通弾"] = "略微跟踪的高威力贯通弹",
        ["当った敵弾を全てホーミング弾に変えて攻撃する"] = "将击中的敌弹全部变为跟踪弹反击",
        ["画面内の全てのアイテムを回収できる"] = "可回收画面内的所有道具",
        ["操作：全てのボタンを離す"] = "操作：松开所有按键",
        ["移動方向と逆方向に攻撃が出来る"] = "可向移动的反方向攻击",
        ["低速時に固定可能"] = "低速时可固定",
        ["一定時間自分の周りを攻撃する"] = "一定时间内攻击自己周围",
        ["超高速移動が出来る"] = "可超高速移动",
        ["操作：ショットボタンを離す"] = "操作：松开射击键",
        ["高速移動だと集中、低速移動だと広範囲攻撃"] = "高速移动时集中、低速移动时广范围攻击",
        ["やや速度の遅いショット"] = "速度稍慢的射击",
        ["人形を犠牲にしてやや強力なレーザーを撃つ"] = "牺牲人偶发射稍强的激光",
        ["オプションを８つまで付けられる"] = "最多可装备 8 个子机",
        ["霊撃を最大８回ストックできる"] = "灵击最多可储存 8 次",
        ["火符 前方集中配置　水符 前方広範囲配置"] = "火符 前方集中配置　水符 前方广范围配置",
        ["木符 斜め攻撃配置　金符 左右攻撃配置"] = "木符 斜向攻击配置　金符 左右攻击配置",
        ["土符 後方攻撃配置"] = "土符 后方攻击配置",
        ["オプションの配置を切り替えられる"] = "可切换子机的配置",
        ["操作：ショットと低速ボタンの同時押し"] = "操作：同时按下射击与低速键",
        ["後方から強力ミサイルを放つ"] = "从后方发射强力导弹",
        ["一定時間バリアを張る。"] = "一定时间张开屏障。",
        ["バリア中に被弾すると画面内の敵弾を全て消す。"] = "屏障中被弹则消除画面内所有敌弹。",
        ["※最後まで被弾しないと少しパワーが回復します"] = "※若始终未被弹则回复少许 Power",
        ["霊撃を使っても被弾しない限り霊撃を使用した事にならない"] = "使用灵击后只要未被弹就不算使用",
        // 暂停/录像菜单（pause.png）
        ["一時停止"] = "暂停", ["満身創痍"] = "满身疮痍", ["再生終了"] = "播放完毕",
        ["一時停止を解除する"] = "解除暂停", ["タイトルへ戻る"] = "回到标题",
        ["はい"] = "是", ["いいえ"] = "否",
        ["最初からやり直す"] = "从头重新开始", ["リプレイを保存して終了"] = "保存录像并结束",
        ["本当にいいですか？"] = "确定吗？", ["リプレイを保存する"] = "保存录像",
        ["もう一度再生する"] = "再播放一次", ["続けて再挑戦する"] = "继续挑战",
        ["一時停止。"] = "暂停", ["次シーンへ"] = "进入下一场景", ["タイトル画面へ戻る"] = "回到标题画面",
        ["満身創痍！"] = "满身疮痍！", ["継続する"] = "继续", ["本当に？"] = "确定吗？", ["演目終了！"] = "演出结束！",
        // 结算/加载
        ["撃破時間"] = "击破时间", ["実時間"] = "实际时间", ["少女祈禱中"] = "少女祈祷中",
        // 关卡标题卡（th11 主题 + 地名，th10 地名）
        ["忘恩の地から吹く風"] = "从忘恩之地吹来的风", ["地上と過去を結ぶ深道"] = "连接地上与过去的深道",
        ["忘れられた雪の旧都"] = "被遗忘的雪之旧都", ["誰からも好かれない恐怖の目"] = "不受任何人喜爱的恐怖之眼",
        ["昔時の業火"] = "往昔的业火", ["荒々しき二つ目の太陽"] = "狂暴的第二个太阳",
        ["地獄のラブリービジター"] = "地狱的可爱访客",
        ["幻想風穴"] = "幻想风穴", ["地獄の深道"] = "地狱的深道", ["旧地獄街道"] = "旧地狱街道",
        ["地霊殿"] = "地灵殿", ["灼熱地獄跡"] = "灼热地狱遗迹", ["地底都市最深部"] = "地底都市最深部",
        ["守矢神社"] = "守矢神社",
        ["妖怪の山の麓"] = "妖怪之山山麓", ["妖怪の樹海"] = "妖怪的树海", ["未踏の渓谷"] = "未踏的溪谷",
        ["九天の滝"] = "九天瀑布", ["守矢の神社"] = "守矢神社", ["風神の湖"] = "风神之湖",
        ["守矢の神社（本殿）"] = "守矢神社（正殿）",
    };
}
