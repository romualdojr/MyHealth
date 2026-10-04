namespace MyHealth.Web.Common;

/// <summary>
/// The Tailwind CSS class combinations the dashboard and back office repeat (buttons, fields, cards, tables, badges,
/// alerts), so every page looks the same without component CSS. Use them as <c>class="@Ui.Button"</c>; add one-off
/// utilities next to them with string interpolation. Tailwind finds these classes by scanning this file, so keep each
/// class name written out in full (never build names like <c>"bg-" + color</c>).
/// <para>
/// Mobile first: on phones (below the <c>sm</c> breakpoint) every control is at least 44 px tall and fields use 16 px
/// text, so nothing is hard to tap and iOS never zooms in on a field; from <c>sm</c> up the compact desktop sizes apply.
/// </para>
/// </summary>
public static class Ui
{
    // Typography

    public const string PageTitle = "text-xl font-semibold tracking-tight text-slate-900 sm:text-2xl";
    public const string SectionTitle = "text-lg font-semibold text-slate-900";
    public const string CardTitle = "text-sm font-semibold text-slate-900";
    public const string Muted = "text-slate-500";
    public const string Help = "text-sm text-slate-500";
    public const string Link = "font-medium text-teal-700 hover:text-teal-900 hover:underline";
    public const string Breadcrumb = "mb-2 text-sm text-slate-500";

    /// <summary>A link that is a tap target of its own (breadcrumbs, "Abrir plano"): 44 px tall on phones.</summary>
    public const string TapLink = "inline-flex min-h-11 items-center font-medium text-teal-700 hover:text-teal-900 hover:underline sm:min-h-0";

    // Surfaces

    public const string Card = "rounded-xl border border-slate-200 bg-white shadow-sm";
    public const string CardBody = "p-4 sm:p-5";

    /// <summary>A card that asks for attention: a form being filled, a confirmation.</summary>
    public const string CardHighlight = "rounded-xl border border-teal-300 bg-white shadow-sm ring-1 ring-teal-100";
    public const string CardDanger = "rounded-xl border border-red-300 bg-white shadow-sm ring-1 ring-red-100";
    public const string CardHeader = "flex flex-wrap items-center gap-2 border-b border-slate-200 bg-slate-50 px-4 py-3 rounded-t-xl";
    public const string EmptyState = "rounded-xl border border-dashed border-slate-300 bg-white px-6 py-10 text-center text-slate-500";
    public const string Divider = "my-4 border-slate-200";

    /// <summary>
    /// A list of cards that replaces a table on phones: <c>&lt;div class="@Ui.List md:hidden"&gt;</c> with
    /// <see cref="ListItem"/> rows, next to the table wrapped in <c>hidden md:block</c>.
    /// </summary>
    public const string List = "divide-y divide-slate-200 overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm";
    public const string ListItem = "block px-4 py-3";

    /// <summary>A whole row that is tapped to open something (a client, a workout).</summary>
    public const string ListItemLink = "flex min-h-14 items-center gap-3 px-4 py-3 hover:bg-slate-50 active:bg-slate-100";

    /// <summary>
    /// A panel that is a full-height bottom sheet on phones and an inline card from <c>sm</c> up (exercise picker,
    /// forms that open in place).
    /// </summary>
    public const string Sheet =
        "fixed inset-x-0 bottom-0 z-50 max-h-[90dvh] overflow-y-auto rounded-t-2xl border-t border-slate-200 bg-white p-4 pb-[max(1rem,env(safe-area-inset-bottom))] shadow-2xl " +
        "sm:static sm:z-auto sm:max-h-none sm:rounded-xl sm:border sm:border-teal-300 sm:p-5 sm:shadow-sm sm:ring-1 sm:ring-teal-100";

    /// <summary>The dimmed background behind a <see cref="Sheet"/> on phones.</summary>
    public const string SheetBackdrop = "fixed inset-0 z-40 bg-slate-900/40 sm:hidden";

    // Buttons (regular size, then small): 44 px on phones, compact from sm up.

    private const string ButtonBase =
        "inline-flex items-center justify-center gap-2 rounded-lg font-medium transition-colors select-none focus-visible:outline-2 focus-visible:outline-offset-2 " +
        "focus-visible:outline-teal-600 disabled:pointer-events-none disabled:opacity-50 cursor-pointer active:scale-[0.98]";

    private const string Regular = " min-h-11 px-4 text-base sm:min-h-10 sm:text-sm";
    private const string Small = " min-h-11 px-3 text-sm sm:min-h-8 sm:px-2.5 sm:text-xs";

    public const string Button = ButtonBase + Regular + " bg-teal-700 text-white shadow-sm hover:bg-teal-800";
    public const string ButtonSecondary = ButtonBase + Regular + " border border-slate-300 bg-white text-slate-700 shadow-sm hover:bg-slate-50";
    public const string ButtonOutline = ButtonBase + Regular + " border border-teal-600 bg-white text-teal-700 hover:bg-teal-50";
    public const string ButtonDanger = ButtonBase + Regular + " bg-red-600 text-white shadow-sm hover:bg-red-700";
    public const string ButtonDangerOutline = ButtonBase + Regular + " border border-red-300 bg-white text-red-700 hover:bg-red-50";
    public const string ButtonSuccess = ButtonBase + Regular + " bg-emerald-600 text-white shadow-sm hover:bg-emerald-700";
    public const string ButtonSuccessOutline = ButtonBase + Regular + " border border-emerald-600 bg-white text-emerald-700 hover:bg-emerald-50";
    public const string ButtonLink = ButtonBase + " min-h-11 px-2 text-base text-teal-700 hover:text-teal-900 hover:underline sm:min-h-10 sm:text-sm";

    public const string ButtonSm = ButtonBase + Small + " bg-teal-700 text-white shadow-sm hover:bg-teal-800";
    public const string ButtonSecondarySm = ButtonBase + Small + " border border-slate-300 bg-white text-slate-700 hover:bg-slate-50";
    public const string ButtonOutlineSm = ButtonBase + Small + " border border-teal-600 bg-white text-teal-700 hover:bg-teal-50";
    public const string ButtonDangerSm = ButtonBase + Small + " bg-red-600 text-white hover:bg-red-700";
    public const string ButtonDangerOutlineSm = ButtonBase + Small + " border border-red-300 bg-white text-red-700 hover:bg-red-50";
    public const string ButtonSuccessOutlineSm = ButtonBase + Small + " border border-emerald-600 bg-white text-emerald-700 hover:bg-emerald-50";
    public const string ButtonSuccessSm = ButtonBase + Small + " bg-emerald-600 text-white hover:bg-emerald-700";
    public const string ButtonLinkSm = ButtonBase + " min-h-11 px-2 text-sm text-teal-700 hover:text-teal-900 hover:underline sm:min-h-8 sm:px-1.5 sm:text-xs";

    /// <summary>A small square icon button (move up/down, remove): 44 px on phones.</summary>
    public const string IconButton = ButtonBase + " size-11 shrink-0 border border-slate-300 bg-white text-slate-600 hover:bg-slate-50 sm:size-8";
    public const string IconButtonDanger = ButtonBase + " size-11 shrink-0 border border-red-300 bg-white text-red-600 hover:bg-red-50 sm:size-8";

    /// <summary>The ✕ that closes a panel.</summary>
    public const string CloseButton =
        "inline-flex size-11 shrink-0 cursor-pointer items-center justify-center rounded-lg text-slate-400 hover:bg-slate-100 hover:text-slate-700 sm:size-8";

    /// <summary>A pill used for quick choices (filters, suggested prompts): horizontally scrollable rows on phones.</summary>
    public const string Chip =
        "inline-flex min-h-11 shrink-0 cursor-pointer items-center rounded-full border border-slate-300 bg-white px-4 text-sm font-medium text-slate-700 " +
        "hover:bg-slate-50 active:bg-slate-100 disabled:opacity-50 sm:min-h-8 sm:px-3 sm:text-xs aria-pressed:border-teal-600 aria-pressed:bg-teal-50 aria-pressed:text-teal-800";

    /// <summary>A row of chips that scrolls sideways on phones instead of wrapping into many lines.</summary>
    public const string ChipRow = "-mx-4 flex gap-2 overflow-x-auto px-4 pb-1 [scrollbar-width:none] sm:mx-0 sm:flex-wrap sm:overflow-visible sm:px-0";

    // Forms: 16 px text on phones (iOS zooms in on smaller fields), 44 px tall.

    public const string Label = "mb-1 block text-sm font-medium text-slate-700";
    public const string Input =
        "block min-h-11 w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-base text-slate-900 shadow-sm placeholder:text-slate-400 " +
        "focus:border-teal-600 focus:outline-none focus:ring-2 focus:ring-teal-600/20 disabled:bg-slate-100 disabled:text-slate-500 sm:min-h-10 sm:text-sm";
    public const string InputSm =
        "block min-h-11 w-full rounded-md border border-slate-300 bg-white px-3 py-1 text-base text-slate-900 placeholder:text-slate-400 " +
        "focus:border-teal-600 focus:outline-none focus:ring-2 focus:ring-teal-600/20 disabled:bg-slate-100 sm:min-h-8 sm:px-2 sm:text-sm";
    public const string Select = Input;
    public const string SelectSm = InputSm;
    public const string Textarea = Input;
    public const string Checkbox = "size-5 shrink-0 rounded border-slate-300 accent-teal-700 sm:size-4";
    public const string CheckLabel = "inline-flex min-h-11 items-center gap-3 text-base text-slate-700 sm:min-h-0 sm:gap-2 sm:text-sm";

    /// <summary>A text addon inside an input group (e.g. "kg", "s").</summary>
    public const string InputAddon = "inline-flex items-center border border-slate-300 bg-slate-50 px-2 text-sm text-slate-500 sm:text-xs";

    // Tables (from md up; phones get a List of cards instead)

    public const string TableWrap = "overflow-x-auto rounded-xl border border-slate-200 bg-white shadow-sm";
    public const string Table = "min-w-full text-sm";
    public const string THead = "bg-slate-50";
    public const string Th = "px-3 py-2 text-left text-xs font-semibold uppercase tracking-wide text-slate-500";
    public const string TBody = "divide-y divide-slate-100";
    public const string Td = "px-3 py-2 align-middle text-slate-700";
    public const string RowWarning = "bg-amber-50";
    public const string RowMuted = "bg-slate-50";

    // Badges

    private const string BadgeBase = "inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium whitespace-nowrap";

    public const string BadgeSuccess = BadgeBase + " bg-emerald-100 text-emerald-800";
    public const string BadgeWarning = BadgeBase + " bg-amber-100 text-amber-800";
    public const string BadgeDanger = BadgeBase + " bg-red-100 text-red-800";
    public const string BadgeInfo = BadgeBase + " bg-sky-100 text-sky-800";
    public const string BadgePrimary = BadgeBase + " bg-teal-100 text-teal-800";
    public const string BadgeNeutral = BadgeBase + " bg-slate-100 text-slate-700";
    public const string BadgeDark = BadgeBase + " bg-slate-800 text-white";

    // Alerts

    public const string AlertDanger = "rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800";
    public const string AlertWarning = "rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900";
    public const string AlertInfo = "rounded-lg border border-sky-200 bg-sky-50 px-4 py-3 text-sm text-sky-900";
    public const string AlertSuccess = "rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-900";
    public const string AlertNeutral = "rounded-lg border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-700";

    // Navigation (sidebar from lg up, drawer on phones)

    public const string NavSection = "px-3 pt-5 pb-1 text-xs font-semibold uppercase tracking-wider text-slate-400";

    /// <summary>For <c>NavLink</c>: it marks the current page with <c>aria-current="page"</c>, which highlights it.</summary>
    public const string NavLink =
        "flex min-h-11 items-center rounded-lg px-3 text-base font-medium text-slate-300 hover:bg-slate-800 hover:text-white lg:min-h-9 lg:text-sm " +
        "aria-[current=page]:bg-slate-800 aria-[current=page]:text-white";

    /// <summary>An item of the bottom tab bar on phones (icon above a short label).</summary>
    public const string TabLink =
        "flex min-h-14 flex-1 flex-col items-center justify-center gap-0.5 text-[11px] font-medium text-slate-500 active:bg-slate-100 " +
        "aria-[current=page]:text-teal-700";

    // Progress

    public const string ProgressTrack = "h-2 w-full overflow-hidden rounded-full bg-slate-200";
    public const string ProgressBar = "h-full rounded-full bg-teal-600 transition-all";
}
