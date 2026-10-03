namespace MyHealth.Web.Common;

/// <summary>
/// The Tailwind CSS class combinations the dashboard and back office repeat (buttons, fields, cards, tables, badges,
/// alerts), so every page looks the same without component CSS. Use them as <c>class="@Ui.Button"</c>; add one-off
/// utilities next to them with string interpolation. Tailwind finds these classes by scanning this file, so keep each
/// class name written out in full (never build names like <c>"bg-" + color</c>).
/// </summary>
public static class Ui
{
    // Typography

    public const string PageTitle = "text-2xl font-semibold tracking-tight text-slate-900";
    public const string SectionTitle = "text-lg font-semibold text-slate-900";
    public const string CardTitle = "text-sm font-semibold text-slate-900";
    public const string Muted = "text-slate-500";
    public const string Help = "text-sm text-slate-500";
    public const string Link = "font-medium text-teal-700 hover:text-teal-900 hover:underline";
    public const string Breadcrumb = "mb-2 text-sm text-slate-500";

    // Surfaces

    public const string Card = "rounded-xl border border-slate-200 bg-white shadow-sm";
    public const string CardBody = "p-4 sm:p-5";

    /// <summary>A card that asks for attention: a form being filled, a confirmation.</summary>
    public const string CardHighlight = "rounded-xl border border-teal-300 bg-white shadow-sm ring-1 ring-teal-100";
    public const string CardDanger = "rounded-xl border border-red-300 bg-white shadow-sm ring-1 ring-red-100";
    public const string CardHeader = "flex flex-wrap items-center gap-2 border-b border-slate-200 bg-slate-50 px-4 py-3 rounded-t-xl";
    public const string EmptyState = "rounded-xl border border-dashed border-slate-300 bg-white px-6 py-10 text-center text-slate-500";
    public const string Divider = "my-4 border-slate-200";

    // Buttons (regular size, then small)

    private const string ButtonBase =
        "inline-flex items-center justify-center gap-2 rounded-lg font-medium transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 " +
        "focus-visible:outline-teal-600 disabled:pointer-events-none disabled:opacity-50 cursor-pointer";

    public const string Button = ButtonBase + " px-4 py-2 text-sm bg-teal-700 text-white shadow-sm hover:bg-teal-800";
    public const string ButtonSecondary = ButtonBase + " px-4 py-2 text-sm border border-slate-300 bg-white text-slate-700 shadow-sm hover:bg-slate-50";
    public const string ButtonOutline = ButtonBase + " px-4 py-2 text-sm border border-teal-600 bg-white text-teal-700 hover:bg-teal-50";
    public const string ButtonDanger = ButtonBase + " px-4 py-2 text-sm bg-red-600 text-white shadow-sm hover:bg-red-700";
    public const string ButtonDangerOutline = ButtonBase + " px-4 py-2 text-sm border border-red-300 bg-white text-red-700 hover:bg-red-50";
    public const string ButtonSuccess = ButtonBase + " px-4 py-2 text-sm bg-emerald-600 text-white shadow-sm hover:bg-emerald-700";
    public const string ButtonSuccessOutline = ButtonBase + " px-4 py-2 text-sm border border-emerald-600 bg-white text-emerald-700 hover:bg-emerald-50";
    public const string ButtonLink = ButtonBase + " px-2 py-2 text-sm text-teal-700 hover:text-teal-900 hover:underline";

    public const string ButtonSm = ButtonBase + " px-2.5 py-1.5 text-xs bg-teal-700 text-white shadow-sm hover:bg-teal-800";
    public const string ButtonSecondarySm = ButtonBase + " px-2.5 py-1.5 text-xs border border-slate-300 bg-white text-slate-700 hover:bg-slate-50";
    public const string ButtonOutlineSm = ButtonBase + " px-2.5 py-1.5 text-xs border border-teal-600 bg-white text-teal-700 hover:bg-teal-50";
    public const string ButtonDangerSm = ButtonBase + " px-2.5 py-1.5 text-xs bg-red-600 text-white hover:bg-red-700";
    public const string ButtonDangerOutlineSm = ButtonBase + " px-2.5 py-1.5 text-xs border border-red-300 bg-white text-red-700 hover:bg-red-50";
    public const string ButtonSuccessOutlineSm = ButtonBase + " px-2.5 py-1.5 text-xs border border-emerald-600 bg-white text-emerald-700 hover:bg-emerald-50";
    public const string ButtonSuccessSm = ButtonBase + " px-2.5 py-1.5 text-xs bg-emerald-600 text-white hover:bg-emerald-700";
    public const string ButtonLinkSm = ButtonBase + " px-1.5 py-1 text-xs text-teal-700 hover:text-teal-900 hover:underline";

    /// <summary>A small square icon button (move up/down, remove).</summary>
    public const string IconButton = ButtonBase + " size-8 border border-slate-300 bg-white text-slate-600 hover:bg-slate-50";
    public const string IconButtonDanger = ButtonBase + " size-8 border border-red-300 bg-white text-red-600 hover:bg-red-50";

    /// <summary>The ✕ that closes a panel.</summary>
    public const string CloseButton = "rounded-md p-1 text-slate-400 hover:bg-slate-100 hover:text-slate-700 cursor-pointer";

    // Forms

    public const string Label = "mb-1 block text-sm font-medium text-slate-700";
    public const string Input =
        "block w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm placeholder:text-slate-400 " +
        "focus:border-teal-600 focus:outline-none focus:ring-2 focus:ring-teal-600/20 disabled:bg-slate-100 disabled:text-slate-500";
    public const string InputSm =
        "block w-full rounded-md border border-slate-300 bg-white px-2 py-1 text-sm text-slate-900 placeholder:text-slate-400 " +
        "focus:border-teal-600 focus:outline-none focus:ring-2 focus:ring-teal-600/20 disabled:bg-slate-100";
    public const string Select = Input;
    public const string SelectSm = InputSm;
    public const string Textarea = Input;
    public const string Checkbox = "size-4 rounded border-slate-300 accent-teal-700";
    public const string CheckLabel = "inline-flex items-center gap-2 text-sm text-slate-700";

    /// <summary>A text addon inside an input group (e.g. "kg", "s").</summary>
    public const string InputAddon = "inline-flex items-center border border-slate-300 bg-slate-50 px-2 text-xs text-slate-500";

    // Tables

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

    // Navigation (sidebar)

    public const string NavSection = "px-3 pt-5 pb-1 text-xs font-semibold uppercase tracking-wider text-slate-400";
    /// <summary>For <c>NavLink</c>: it marks the current page with <c>aria-current="page"</c>, which highlights it.</summary>
    public const string NavLink =
        "flex items-center rounded-lg px-3 py-2 text-sm font-medium text-slate-300 hover:bg-slate-800 hover:text-white " +
        "aria-[current=page]:bg-slate-800 aria-[current=page]:text-white";

    // Progress

    public const string ProgressTrack = "h-2 w-full overflow-hidden rounded-full bg-slate-200";
    public const string ProgressBar = "h-full rounded-full bg-teal-600 transition-all";
}
