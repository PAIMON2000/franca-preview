namespace Franca.Services;

// 内置词典种子（lab/010 决策 4）：仅 en→zh-CN 做技术验证，UI 高频短语，质量优先于数量。
// 每行 "src<TAB>tgt"；查找本身大小写不敏感，种子保持游戏 UI 常见书写形式。
internal static class DictionarySeeds
{
    public static IReadOnlyDictionary<string, string>? TryGet(string from, string to)
    {
        if (!from.Equals("en", StringComparison.OrdinalIgnoreCase)) return null;
        if (!to.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)) return null;
        return EnToZh.Value;
    }

    private static readonly Lazy<Dictionary<string, string>> EnToZh = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Table.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) continue;
            var tab = trimmed.IndexOf('\t');
            map[trimmed[..tab]] = trimmed[(tab + 1)..];
        }
        return map;
    });

    private const string Table = """
        New Game	新游戏
        Continue	继续
        Load Game	读取游戏
        Save Game	保存游戏
        Load	读取
        Save	保存
        Quick Save	快速保存
        Quick Load	快速读取
        Auto Save	自动保存
        Load Menu	读取菜单
        Save Slot	存档槽
        Empty Slot	空槽位
        Empty	空
        Settings	设置
        Options	选项
        Preferences	偏好设置
        Main Menu	主菜单
        Menu	菜单
        Pause	暂停
        Paused	已暂停
        Resume	继续游戏
        Restart	重新开始
        Retry	重试
        Quit	退出
        Exit	退出
        Back	返回
        Cancel	取消
        Confirm	确认
        OK	确定
        Apply	应用
        Defaults	恢复默认
        Reset	重置
        Delete	删除
        Remove	移除
        Create	创建
        Close	关闭
        Open	打开
        Send	发送
        Yes	是
        No	否
        On	开
        Off	关
        Enable	启用
        Disable	禁用
        Enabled	已启用
        Disabled	已禁用
        Locked	已锁定
        Unlocked	已解锁
        Unlock	解锁
        Play	开始游戏
        Start	开始
        Started	已开始
        Chapter	章节
        Prologue	序章
        Epilogue	尾声
        Level	等级
        Difficulty	难度
        Easy	简单
        Normal	普通
        Hard	困难
        Very Hard	非常困难
        Expert	专家
        Master	大师
        Novice	新手
        Beginner	初学者
        Inventory	物品栏
        Bag	背包
        Item	物品
        Items	物品
        Consumables	消耗品
        Materials	材料
        Key Items	关键道具
        Equipment	装备
        Equip	装备
        Equipped	已装备
        Unequip	卸下
        Weapon	武器
        Weapons	武器
        Armor	护甲
        Accessory	饰品
        Quest	任务
        Quests	任务
        Mission	任务
        Missions	任务
        Main Quest	主线任务
        Side Quest	支线任务
        Objective	目标
        Objectives	目标
        Map	地图
        World Map	世界地图
        Skill	技能
        Skills	技能
        Skill Tree	技能树
        Passive	被动
        Passive Skill	被动技能
        Active Skill	主动技能
        Ultimate	终极技能
        Ability	能力
        Abilities	能力
        Shop	商店
        Store	商店
        Buy	购买
        Sell	出售
        Purchase	购买
        Price	价格
        Cost	花费
        Owned	已拥有
        Reward	奖励
        Rewards	奖励
        Loot	战利品
        Chest	宝箱
        Gallery	画廊
        Extras	额外内容
        Achievements	成就
        Statistics	统计
        Leaderboard	排行榜
        Ranking	排名
        Rank	段位
        Credits	制作名单
        Profile	档案
        Character	角色
        Characters	角色
        Enemy	敌人
        Boss	Boss
        Ally	同伴
        Allies	同伴
        Party	队伍
        Team	队伍
        Player	玩家
        NPC	NPC
        Health	生命值
        Mana	法力值
        HP	生命值
        MP	法力值
        Stamina	耐力
        Experience	经验值
        EXP	经验
        Level Up	升级
        Gold	金币
        Coins	金币
        Money	金钱
        Points	点数
        Score	分数
        Attack	攻击
        Defense	防御
        Damage	伤害
        Critical	暴击
        Critical Hit	暴击
        Critical Damage	暴击伤害
        Speed	速度
        Strength	力量
        Agility	敏捷
        Intelligence	智力
        Luck	幸运
        Dodge	闪避
        Block	格挡
        Parry	招架
        Counter	反击
        Combo	连击
        Cooldown	冷却
        Turn	回合
        Round	回合
        Your Turn	你的回合
        Victory	胜利
        Defeat	失败
        You Win	你赢了
        You Lose	你输了
        Game Over	游戏结束
        Checkpoint	检查点
        Respawn	重生
        Revive	复活
        Heal	治疗
        Healing	治疗
        Potion	药水
        Elixir	灵药
        Antidote	解毒剂
        Poisoned	中毒
        Buff	增益
        Debuff	减益
        Status	状态
        Status Effects	状态效果
        Element	属性
        Fire	火
        Ice	冰
        Lightning	雷
        Wind	风
        Earth	土
        Water	水
        Light	光
        Dark	暗
        Physical	物理
        Magic	魔法
        Melee	近战
        Ranged	远程
        Ammo	弹药
        Arrow	箭矢
        Bullet	子弹
        Durability	耐久
        Upgrade	升级
        Enhance	强化
        Craft	制作
        Crafting	合成
        Recipe	配方
        Ingredient	材料
        Enchant	附魔
        Gem	宝石
        Rarity	稀有度
        Common	普通
        Uncommon	优秀
        Rare	稀有
        Epic	史诗
        Legendary	传说
        Mythic	神话
        Tier	阶级
        Rating	评分
        Audio	音频
        Sound	音效
        Music	音乐
        Volume	音量
        Graphics	图形
        Video	视频
        Resolution	分辨率
        Fullscreen	全屏
        Windowed	窗口化
        Quality	画质
        Brightness	亮度
        Shadow	阴影
        Controls	操作
        Keyboard	键盘
        Mouse	鼠标
        Controller	手柄
        Gamepad	手柄
        Sensitivity	灵敏度
        Vibration	振动
        Text Speed	文字速度
        Language	语言
        Auto	自动
        Skip	跳过
        Log	日志
        History	历史
        Dialogue	对话
        Help	帮助
        Tutorial	教程
        Tips	提示
        Hint	提示
        New	新建
        Name	名称
        Rename	重命名
        Join	加入
        Leave	离开
        Friends	好友
        Online	在线
        Offline	离线
        Singleplayer	单人游戏
        Multiplayer	多人游戏
        Co-op	合作模式
        Season	赛季
        Daily	每日
        Weekly	每周
        Challenge	挑战
        Challenges	挑战
        Event	活动
        Events	活动
        News	新闻
        Announcements	公告
        Patch Notes	更新日志
        Trophy	奖杯
        Medal	勋章
        Title	称号
        Avatar	头像
        Skin	皮肤
        Outfit	服装
        Costume	服装
        Pet	宠物
        Mount	坐骑
        Travel	传送
        Fast Travel	快速传送
        Dungeon	地下城
        Arena	竞技场
        Stage	关卡
        Wave	波次
        Key	钥匙
        Door	门
        Gate	大门
        Portal	传送门
        Requires	需要
        Loading	加载中
        Saving	保存中
        Connecting	连接中
        Downloading	下载中
        Installing	安装中
        Update	更新
        Version	版本
        Account	账户
        Login	登录
        Logout	登出
        Register	注册
        Password	密码
        Username	用户名
        Email	邮箱
        """;
}
