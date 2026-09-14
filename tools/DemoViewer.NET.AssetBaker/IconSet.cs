namespace DemoViewer.NET.AssetBaker;

/// <summary>
///     Which CS2 icons get baked, and what they are called on our side. <b>This file is the curation</b>:
///     everything else in the icon bake is mechanism.
///     <para>
///         Three namespaces, because they have three different key sources and conflating them makes
///         lookup ambiguous:
///     </para>
///     <list type="bullet">
///         <item>
///             <c>equipment/*</c> — keyed by the raw <c>player_death.weapon</c> string. Valve's own file
///             names already match it exactly (<c>ak47</c>, <c>usp_silencer</c>, <c>knife_t</c>,
///             <c>inferno</c>, <c>taser</c>), so this namespace takes the whole directory and needs no
///             mapping table to drift out of date.
///         </item>
///         <item>
///             <c>modifier/*</c> — renamed from Valve's names to <c>KillFeedRow</c>'s property names, so
///             the kill feed maps a flag to an icon without a lookup table either.
///         </item>
///         <item>
///             <c>ui/*</c> — hand-picked round, economy and scoreboard vocabulary. The only namespace
///             where adding an icon is a judgement call rather than a consequence.
///         </item>
///     </list>
/// </summary>
public static class IconSet
{
    /// <summary>Everything under here is baked, one icon per file, under the name Valve gave it.</summary>
    public const string EquipmentDir = "panorama/images/icons/equipment/";


    /// <summary>
    ///     Per-map icons, taken as a directory like <see cref="EquipmentDir" /> and keyed by the bare map
    ///     name so a caller passes the map it already has: <c>map_icon_de_dust2</c> becomes
    ///     <c>map/de_dust2</c>.
    ///     <para>
    ///         Full-colour miniature map renders, not glyphs, so they bake as pictures. The map-veto
    ///         placeholder in the same directory is skipped - it names no map.
    ///     </para>
    /// </summary>
    public const string MapIconDir = "panorama/images/map_icons/";

    /// <summary>The filename prefix stripped to get the map name.</summary>
    public const string MapIconPrefix = "map_icon_";

    /// <summary>Not a map: the veto-screen placeholder.</summary>
    public const string MapIconSkip = "map_icon_lobby_mapveto";

    /// <summary>The kill-feed modifiers, mapped from Valve's file name to the flag they stand for.</summary>
    public static readonly IReadOnlyDictionary<string, string> Modifiers = new Dictionary<string, string>
    {
        ["panorama/images/hud/deathnotice/icon_headshot"] = "headshot",
        ["panorama/images/hud/deathnotice/noscope"] = "noscope",
        ["panorama/images/hud/deathnotice/penetrate"] = "penetrate",
        ["panorama/images/hud/deathnotice/smoke_kill"] = "smoke",
        ["panorama/images/hud/deathnotice/blind_kill"] = "blind",
        ["panorama/images/hud/deathnotice/inairkill"] = "inair",
        ["panorama/images/hud/deathnotice/icon_suicide"] = "suicide",
        ["panorama/images/hud/deathnotice/domination"] = "domination",
        ["panorama/images/hud/deathnotice/revenge"] = "revenge"
    };

    /// <summary>
    ///     General UI icons, mapped from source path to our name. Renamed where Valve's name is an
    ///     implementation detail (<c>map_bombzone_a</c> → <c>bombsite_a</c>) and left alone where it is not.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Ui = new Dictionary<string, string>
    {
        // Bomb and defuse
        ["panorama/images/icons/ui/bomb"] = "bomb",
        ["panorama/images/icons/ui/bomb_c4"] = "bomb_c4",
        ["panorama/images/icons/ui/defuser"] = "defuser",
        ["panorama/images/icons/ui/map_bombzone_a"] = "bombsite_a",
        ["panorama/images/icons/ui/map_bombzone_b"] = "bombsite_b",
        ["panorama/images/icons/ui/map_bomb_above"] = "bomb_above",
        ["panorama/images/icons/ui/map_bomb_below"] = "bomb_below",
        ["panorama/images/icons/ui/map_defuse"] = "defuse",

        // Round and clock
        ["panorama/images/icons/ui/clock"] = "clock",
        ["panorama/images/icons/ui/timer"] = "timer",
        ["panorama/images/icons/ui/timer_short"] = "timer_short",
        ["panorama/images/icons/ui/timer_long"] = "timer_long",
        ["panorama/images/icons/ui/winner_ribbon"] = "winner",
        ["panorama/images/icons/ui/trophy"] = "trophy",

        // Scoreboard and stats
        ["panorama/images/icons/ui/kill"] = "kill",
        ["panorama/images/icons/ui/kill_headshot"] = "kill_headshot",
        ["panorama/images/icons/ui/map_death"] = "death",
        ["panorama/images/icons/ui/stat_assists"] = "assists",
        ["panorama/images/icons/ui/stat_mvps"] = "mvp",
        // NOT stat_rounds_3k/4k/5k: those are 13x18 card PLATES, with the numeral drawn as Panorama
        // text on top, so they bake as solid blocks. CS2 ships no standalone multi-kill artwork — a
        // 3K/4K/5K badge has to be our own composition over ui/star or a plain chip.
        // NOT round_hash either: it is an 18x3.8 divider rule, not a round icon.
        ["panorama/images/icons/ui/star"] = "star",
        ["panorama/images/icons/ui/icon_star"] = "star_filled",
        ["panorama/images/icons/ui/icon_star_empty"] = "star_empty",

        // Player state
        ["panorama/images/hud/health_cross"] = "health",
        ["panorama/images/hud/armor"] = "armor",
        ["panorama/images/hud/armor_helmet"] = "armor_helmet",
        ["panorama/images/icons/ui/player"] = "player",
        ["panorama/images/icons/ui/teamplayer"] = "teamplayer",
        ["panorama/images/icons/ui/bot"] = "bot",
        ["panorama/images/icons/ui/enemyspotted"] = "spotted",

        // Utility and view
        ["panorama/images/icons/ui/map_smoke"] = "smoke",
        ["panorama/images/icons/ui/crosshair"] = "crosshair",
        ["panorama/images/icons/ui/crosshair_circle"] = "crosshair_circle",
        ["panorama/images/icons/ui/zoom_in"] = "zoom_in",
        ["panorama/images/icons/ui/zoom_out"] = "zoom_out",
        ["panorama/images/icons/ui/camera"] = "camera",
        ["panorama/images/icons/ui/broadcast"] = "broadcast",
        ["panorama/images/icons/ui/bullet"] = "bullet",
        ["panorama/images/icons/ui/bullet_shell"] = "bullet_shell",
        ["panorama/images/hud/bullets"] = "ammo",

        // Chrome
        ["panorama/images/icons/ui/alert"] = "alert",
        ["panorama/images/icons/ui/bug"] = "bug",
        ["panorama/images/icons/ui/trade"] = "trade",
        ["panorama/images/icons/ui/filter_team"] = "filter_team",

        // Kill semantics
        ["panorama/images/hud/teamcounter/killtype_headshot"] = "killtype_headshot",
        ["panorama/images/hud/teamcounter/killtype_blast"] = "killtype_blast",
        ["panorama/images/hud/teamcounter/killtype_burn"] = "killtype_burn",
        ["panorama/images/hud/teamcounter/killtype_shock"] = "killtype_shock",
        ["panorama/images/hud/teamcounter/killtype_slash"] = "killtype_slash",
        ["panorama/images/hud/teamcounter/killtype_default"] = "killtype_default",

        // Kill semantics
        ["panorama/images/icons/ui/elimination"] = "elimination",
        ["panorama/images/icons/ui/elimination_headshot"] = "elimination_headshot",
        ["panorama/images/icons/ui/dominated"] = "dominated",
        ["panorama/images/icons/ui/dominated_dead"] = "dominated_dead",
        ["panorama/images/icons/ui/nemesis"] = "nemesis",
        ["panorama/images/icons/ui/nemesis_dead"] = "nemesis_dead",

        // Radar and map markers
        ["panorama/images/icons/ui/map_enemy_onmap"] = "enemy_onmap",
        ["panorama/images/icons/ui/map_enemy_offmap"] = "enemy_offmap",
        ["panorama/images/icons/ui/map_onmap"] = "onmap",
        ["panorama/images/icons/ui/map_ghost"] = "ghost",
        ["panorama/images/icons/ui/map_view_angle"] = "view_angle",
        ["panorama/images/icons/ui/map_view_selected"] = "view_selected",
        ["panorama/images/icons/ui/map_direction_indicator"] = "direction",
        ["panorama/images/icons/ui/map_defused_dropped"] = "defused_dropped",
        ["panorama/images/icons/ui/map_hostage_zone"] = "hostage_zone",
        ["panorama/images/icons/ui/map_hostage_dead"] = "hostage_dead",
        ["panorama/images/icons/ui/buyzone"] = "buyzone",

        // Radar and map markers
        ["panorama/images/hud/reticle/playerid_arrow"] = "player_arrow",
        ["panorama/images/hud/reticle/playerid_arrowfriend"] = "player_arrow_friend",

        // Sides and team
        ["panorama/images/icons/ui/ct_logo_1c"] = "ct_logo",
        ["panorama/images/icons/ui/t_logo_1c"] = "t_logo",
        ["panorama/images/icons/ui/ct_silhouette"] = "ct_silhouette",
        ["panorama/images/icons/ui/teamcolor"] = "team_swatch",
        ["panorama/images/icons/ui/leader"] = "leader",
        ["panorama/images/icons/ui/lobby_leader"] = "lobby_leader",
        ["panorama/images/icons/ui/two_stack"] = "stack_2",
        ["panorama/images/icons/ui/three_stack"] = "stack_3",
        ["panorama/images/icons/ui/four_stack"] = "stack_4",
        ["panorama/images/icons/ui/five_stack"] = "stack_5",

        // Loadout and ammunition
        ["panorama/images/icons/ui/outofammo"] = "out_of_ammo",
        ["panorama/images/icons/ui/helmet"] = "helmet",
        ["panorama/images/icons/ui/defuser_white"] = "defuser_solid",

        // Loadout and ammunition
        ["panorama/images/hud/ammo_reserve"] = "ammo_reserve",
        ["panorama/images/hud/ammo_single"] = "ammo_single",
        ["panorama/images/hud/ammo_burst"] = "ammo_burst",
        ["panorama/images/hud/ammo_fullauto"] = "ammo_fullauto",
        ["panorama/images/hud/bullet_single"] = "fire_single",
        ["panorama/images/hud/bullet_auto"] = "fire_auto",

        // Comms - the radio and chat wheel
        ["panorama/images/icons/ui/chatwheel_enemyspotted"] = "comm_enemy_spotted",
        ["panorama/images/icons/ui/chatwheel_sectorclear"] = "comm_sector_clear",
        ["panorama/images/icons/ui/chatwheel_needbackup"] = "comm_need_backup",
        ["panorama/images/icons/ui/chatwheel_inposition"] = "comm_in_position",
        ["panorama/images/icons/ui/chatwheel_holdposition"] = "comm_hold_position",
        ["panorama/images/icons/ui/chatwheel_followme"] = "comm_follow_me",
        ["panorama/images/icons/ui/chatwheel_gogogo"] = "comm_go",
        ["panorama/images/icons/ui/chatwheel_sitea"] = "comm_site_a",
        ["panorama/images/icons/ui/chatwheel_siteb"] = "comm_site_b",
        ["panorama/images/icons/ui/chatwheel_bombat"] = "comm_bomb_at",
        ["panorama/images/icons/ui/chatwheel_ihavethebomb"] = "comm_have_bomb",
        ["panorama/images/icons/ui/chatwheel_droppedbomb"] = "comm_dropped_bomb",
        ["panorama/images/icons/ui/chatwheel_guardingbomb"] = "comm_guarding_bomb",
        ["panorama/images/icons/ui/chatwheel_smoke"] = "comm_smoke",
        ["panorama/images/icons/ui/chatwheel_flashbang"] = "comm_flash",
        ["panorama/images/icons/ui/chatwheel_grenade"] = "comm_grenade",
        ["panorama/images/icons/ui/chatwheel_decoy"] = "comm_decoy",
        ["panorama/images/icons/ui/chatwheel_fire"] = "comm_fire",
        ["panorama/images/icons/ui/chatwheel_sniperspotted"] = "comm_sniper",
        ["panorama/images/icons/ui/chatwheel_oneenemyhere"] = "comm_one_enemy",
        ["panorama/images/icons/ui/chatwheel_multipleenemieshere"] = "comm_multiple_enemies",
        ["panorama/images/icons/ui/chatwheel_rotatetome"] = "comm_rotate",
        ["panorama/images/icons/ui/chatwheel_spreadout"] = "comm_spread_out",
        ["panorama/images/icons/ui/chatwheel_sticktogether"] = "comm_stick_together",
        ["panorama/images/icons/ui/chatwheel_heardnoise"] = "comm_heard_noise",
        ["panorama/images/icons/ui/chatwheel_requestecoround"] = "comm_eco",
        ["panorama/images/icons/ui/chatwheel_affirmative"] = "comm_yes",
        ["panorama/images/icons/ui/chatwheel_negative"] = "comm_no",

        // Connection and voice
        ["panorama/images/icons/ui/ping_1"] = "ping_1",
        ["panorama/images/icons/ui/ping_2"] = "ping_2",
        ["panorama/images/icons/ui/ping_3"] = "ping_3",
        ["panorama/images/icons/ui/ping_4"] = "ping_4",
        ["panorama/images/icons/ui/sound_1"] = "volume_1",
        ["panorama/images/icons/ui/sound_2"] = "volume_2",
        ["panorama/images/icons/ui/sound_3"] = "volume_3",
        ["panorama/images/icons/ui/sound_off"] = "volume_off",
        ["panorama/images/icons/ui/unmuted"] = "mic",
        ["panorama/images/icons/ui/no_connection"] = "no_connection",

        // Match and mode
        ["panorama/images/icons/ui/deathmatch"] = "mode_deathmatch",
        ["panorama/images/icons/ui/armsrace"] = "mode_armsrace",
        ["panorama/images/icons/ui/retakes"] = "mode_retakes",
        ["panorama/images/icons/ui/training"] = "mode_training",
        ["panorama/images/icons/ui/flyingscoutsman"] = "mode_scoutsman",
        ["panorama/images/icons/ui/demolitionmode"] = "mode_demolition",
        ["panorama/images/icons/ui/scrimcomp2v2"] = "mode_wingman",
        ["panorama/images/icons/ui/overwatch"] = "overwatch",
        ["panorama/images/icons/ui/vacnet"] = "vacnet",
        ["panorama/images/icons/ui/prime"] = "prime",
        ["panorama/images/icons/ui/xp_rank"] = "xp_rank",
        ["panorama/images/icons/ui/major_coin"] = "major_coin",
        ["panorama/images/icons/ui/champions"] = "champions",

        // Demo playback
        ["panorama/images/icons/ui/play"] = "play",
        ["panorama/images/icons/ui/pause"] = "pause",
        ["panorama/images/icons/ui/stop"] = "stop",
        ["panorama/images/icons/ui/skip"] = "skip",
        ["panorama/images/icons/ui/replay"] = "replay",
        ["panorama/images/icons/ui/fast"] = "fast",
        ["panorama/images/icons/ui/first_person"] = "first_person",
        ["panorama/images/icons/ui/watch"] = "watch",
        ["panorama/images/icons/ui/film"] = "film",
        ["panorama/images/icons/ui/video_clip"] = "clip",

        // Economy
        ["panorama/images/icons/ui/dollar_sign"] = "dollar",
        ["panorama/images/icons/ui/coin_stack"] = "coins",
        ["panorama/images/icons/ui/bombwavebonus"] = "bomb_bonus",
    };


    /// <summary>
    ///     The classic Skill Group badges: Competitive and Wingman, ranks 0&#8211;18 plus the states a
    ///     player can be in without one.
    ///     <para>
    ///         <b>These are pictures, not masks.</b> Every badge is full-colour with gradients, so unlike
    ///         the weapon and HUD sets they cannot be tinted &#8212; the bake marks them
    ///         <c>tintable: false</c> and consumers draw them as they are. Danger Zone's badges sit
    ///         beside these in the archive and are deliberately not taken; the mode is retired.
    ///     </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Ranks = new Dictionary<string, string>
    {
        ["panorama/images/icons/skillgroups/skillgroup0"] = "competitive/0",
        ["panorama/images/icons/skillgroups/skillgroup1"] = "competitive/1",
        ["panorama/images/icons/skillgroups/skillgroup2"] = "competitive/2",
        ["panorama/images/icons/skillgroups/skillgroup3"] = "competitive/3",
        ["panorama/images/icons/skillgroups/skillgroup4"] = "competitive/4",
        ["panorama/images/icons/skillgroups/skillgroup5"] = "competitive/5",
        ["panorama/images/icons/skillgroups/skillgroup6"] = "competitive/6",
        ["panorama/images/icons/skillgroups/skillgroup7"] = "competitive/7",
        ["panorama/images/icons/skillgroups/skillgroup8"] = "competitive/8",
        ["panorama/images/icons/skillgroups/skillgroup9"] = "competitive/9",
        ["panorama/images/icons/skillgroups/skillgroup10"] = "competitive/10",
        ["panorama/images/icons/skillgroups/skillgroup11"] = "competitive/11",
        ["panorama/images/icons/skillgroups/skillgroup12"] = "competitive/12",
        ["panorama/images/icons/skillgroups/skillgroup13"] = "competitive/13",
        ["panorama/images/icons/skillgroups/skillgroup14"] = "competitive/14",
        ["panorama/images/icons/skillgroups/skillgroup15"] = "competitive/15",
        ["panorama/images/icons/skillgroups/skillgroup16"] = "competitive/16",
        ["panorama/images/icons/skillgroups/skillgroup17"] = "competitive/17",
        ["panorama/images/icons/skillgroups/skillgroup18"] = "competitive/18",
        ["panorama/images/icons/skillgroups/skillgroup_none"] = "competitive/none",
        ["panorama/images/icons/skillgroups/skillgroup_expired"] = "competitive/expired",
        ["panorama/images/icons/skillgroups/skillgroup_need_wins"] = "competitive/needwins",
        ["panorama/images/icons/skillgroups/wingman0"] = "wingman/0",
        ["panorama/images/icons/skillgroups/wingman1"] = "wingman/1",
        ["panorama/images/icons/skillgroups/wingman2"] = "wingman/2",
        ["panorama/images/icons/skillgroups/wingman3"] = "wingman/3",
        ["panorama/images/icons/skillgroups/wingman4"] = "wingman/4",
        ["panorama/images/icons/skillgroups/wingman5"] = "wingman/5",
        ["panorama/images/icons/skillgroups/wingman6"] = "wingman/6",
        ["panorama/images/icons/skillgroups/wingman7"] = "wingman/7",
        ["panorama/images/icons/skillgroups/wingman8"] = "wingman/8",
        ["panorama/images/icons/skillgroups/wingman9"] = "wingman/9",
        ["panorama/images/icons/skillgroups/wingman10"] = "wingman/10",
        ["panorama/images/icons/skillgroups/wingman11"] = "wingman/11",
        ["panorama/images/icons/skillgroups/wingman12"] = "wingman/12",
        ["panorama/images/icons/skillgroups/wingman13"] = "wingman/13",
        ["panorama/images/icons/skillgroups/wingman14"] = "wingman/14",
        ["panorama/images/icons/skillgroups/wingman15"] = "wingman/15",
        ["panorama/images/icons/skillgroups/wingman16"] = "wingman/16",
        ["panorama/images/icons/skillgroups/wingman17"] = "wingman/17",
        ["panorama/images/icons/skillgroups/wingman18"] = "wingman/18",
        ["panorama/images/icons/skillgroups/wingman_none"] = "wingman/none",
        ["panorama/images/icons/skillgroups/wingman_expired"] = "wingman/expired"
    };

    /// <summary>
    ///     Skill Group display names by rank number, from CS2's own <c>resource/csgo_english.txt</c>
    ///     (<c>skillgroup_0</c> &#8230; <c>skillgroup_18</c>).
    ///     <para>
    ///         <b>Wingman shares this list.</b> The localization has no per-rank Wingman strings &#8212;
    ///         only its own <i>state</i> strings (<c>skillgroup_0wingman</c> and friends) &#8212; so a
    ///         Wingman Master Guardian II is spelled exactly like a Competitive one.
    ///     </para>
    /// </summary>
    public static readonly IReadOnlyList<string> RankNames =
    [
        "No Skill Group",
        "Silver I",
        "Silver II",
        "Silver III",
        "Silver IV",
        "Silver Elite",
        "Silver Elite Master",
        "Gold Nova I",
        "Gold Nova II",
        "Gold Nova III",
        "Gold Nova Master",
        "Master Guardian I",
        "Master Guardian II",
        "Master Guardian Elite",
        "Distinguished Master Guardian",
        "Legendary Eagle",
        "Legendary Eagle Master",
        "Supreme Master First Class",
        "The Global Elite"
    ];

    /// <summary>
    ///     The Premier CS Rating emblem. <b>One piece of artwork, seven colours</b> — CS2 ships a single
    ///     greyscale emblem and washes it per tier, which is why the bake produces the variants rather
    ///     than the archive shipping them.
    /// </summary>
    public const string PremierEmblem = "panorama/images/icons/ui/premier_rating_bg";

    /// <summary>The unranked emblem, drawn before a rating is earned. Never tinted — it has no tier.</summary>
    public const string PremierEmblemUnranked = "panorama/images/icons/ui/premier_rating_bg_none";

    /// <summary>
    ///     The seven CS Rating tiers, lifted verbatim from CS2's own
    ///     <c>panorama/styles/rating_emblem.vcss</c> (<c>@define color-csrating-tier-N</c>). The
    ///     thresholds are that file's script half: <c>clamp(floor(rating / 5000), 0, 6)</c>.
    ///     <para>
    ///         Taken from the game rather than transcribed from a wiki so a Valve retune shows up as a
    ///         re-bake diff instead of as a colour nobody notices is stale.
    ///     </para>
    /// </summary>
    public static readonly IReadOnlyList<PremierTierDef> PremierTiers =
    [
        new("tier0", "#b0c3d9", 0),
        new("tier1", "#8cc6ff", 5000),
        new("tier2", "#6a7dff", 10000),
        new("tier3", "#c166ff", 15000),
        new("tier4", "#f03cff", 20000),
        new("tier5", "#eb4b4b", 25000),
        new("tier6", "#ffd700", 30000)
    ];
}

/// <summary>One CS Rating tier: its key suffix, CS2's wash colour, and the rating it starts at.</summary>
/// <param name="Name">Key suffix, e.g. <c>tier3</c>.</param>
/// <param name="Hex">CS2's own wash colour, <c>#rrggbb</c>.</param>
/// <param name="MinRating">Lowest CS Rating in this tier.</param>
public sealed record PremierTierDef(string Name, string Hex, int MinRating);
