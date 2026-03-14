using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Data;

namespace Inmobiscrap.Services;

public interface IReportService
{
    Task<byte[]> GenerateMarketReportAsync(ReportFilters filters);
    Task<byte[]> GenerateComparisonReportAsync(List<int> propertyIds);
}

public record ReportFilters(
    string? Region       = null,
    string? City         = null,
    string? Neighborhood = null,
    string? PropertyType = null);

public class ReportService : IReportService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<ReportService> _logger;

    public ReportService(ApplicationDbContext context, ILogger<ReportService> logger)
    {
        _context = context;
        _logger  = logger;
    }

    // ══════════════════════════════════════════════════════════════════════
    // ENTRY POINTS
    // ══════════════════════════════════════════════════════════════════════

    public async Task<byte[]> GenerateMarketReportAsync(ReportFilters filters)
    {
        var data = await CollectDataAsync(filters);
        var tex  = BuildLatexDocument(data, filters);
        return await CompileLatexAsync(tex);
    }

    public async Task<byte[]> GenerateComparisonReportAsync(List<int> propertyIds)
    {
        var properties = await _context.Properties
            .Where(p => propertyIds.Contains(p.Id))
            .ToListAsync();

        if (properties.Count < 2)
            throw new InvalidOperationException("Se necesitan al menos 2 propiedades para comparar.");

        var snapshots = await _context.PropertySnapshots
            .Where(s => propertyIds.Contains(s.PropertyId) && s.Price.HasValue && s.Price > 0)
            .OrderBy(s => s.ScrapedAt)
            .ToListAsync();

        var tex = BuildComparisonLatex(properties, snapshots);
        return await CompileLatexAsync(tex);
    }

    // ══════════════════════════════════════════════════════════════════════
    // DATA COLLECTION
    // ══════════════════════════════════════════════════════════════════════

    private async Task<ReportData> CollectDataAsync(ReportFilters f)
    {
        var q = _context.Properties.AsQueryable();

        if (!string.IsNullOrWhiteSpace(f.Region))       q = q.Where(p => p.Region       == f.Region);
        if (!string.IsNullOrWhiteSpace(f.City))         q = q.Where(p => p.City         == f.City);
        if (!string.IsNullOrWhiteSpace(f.Neighborhood)) q = q.Where(p => p.Neighborhood == f.Neighborhood);
        if (!string.IsNullOrWhiteSpace(f.PropertyType)) q = q.Where(p => p.PropertyType == f.PropertyType);

        var total = await q.CountAsync();

        // ── Por tipo + moneda ─────────────────────────────────────────────────
        var byTypeRaw = await q
            .Where(p => p.PropertyType != null && p.Price > 0)
            .GroupBy(p => new { p.PropertyType, Currency = p.Currency ?? "CLP" })
            .Select(g => new TypeCurrencyRow
            {
                Type         = g.Key.PropertyType!,
                Currency     = g.Key.Currency,
                Count        = g.Count(),
                AvgPrice     = g.Average(p => (double)p.Price!),
                MinPrice     = g.Min(p => (double)p.Price!),
                MaxPrice     = g.Max(p => (double)p.Price!),
                AvgBedrooms  = g.Where(p => p.Bedrooms  > 0).Any() ? g.Where(p => p.Bedrooms  > 0).Average(p => (double)p.Bedrooms!)  : null,
                AvgBathrooms = g.Where(p => p.Bathrooms > 0).Any() ? g.Where(p => p.Bathrooms > 0).Average(p => (double)p.Bathrooms!) : null,
                AvgArea      = g.Where(p => p.Area      > 0).Any() ? g.Where(p => p.Area      > 0).Average(p => (double)p.Area!)      : null,
            })
            .ToListAsync();

        var byType = byTypeRaw
            .GroupBy(r => r.Type)
            .Select(g =>
            {
                var dom = g.OrderByDescending(x => x.Count).First();
                dom.Count = g.Sum(x => x.Count);
                return dom;
            })
            .OrderByDescending(r => r.Count)
            .ToList();

        // ── Top comunas — precios separados por moneda ────────────────────────
        var neighborhoodRaw = await q
            .Where(p => (p.Neighborhood != null || p.City != null) && p.Price > 0)
            .GroupBy(p => new { Name = p.Neighborhood ?? p.City ?? "Sin dato", Currency = p.Currency ?? "CLP" })
            .Select(g => new { g.Key.Name, g.Key.Currency, Count = g.Count(), Avg = g.Average(p => (double)p.Price!) })
            .ToListAsync();

        var topNeighborhoods = neighborhoodRaw
            .GroupBy(r => r.Name)
            .Select(g => new NeighborhoodRow
            {
                Name        = g.Key,
                Count       = g.Sum(r => r.Count),
                AvgPriceUF  = g.FirstOrDefault(r => r.Currency == "UF")?.Avg,
                AvgPriceCLP = g.FirstOrDefault(r => r.Currency == "CLP")?.Avg,
            })
            .OrderByDescending(r => r.Count)
            .Take(8)
            .ToList();

        // ── Cambios de precio recientes ───────────────────────────────────────
        var recentChanges = (await q
            .Where(p => p.PriceChangedAt != null && p.PreviousPrice != null && p.Price != null)
            .OrderByDescending(p => p.PriceChangedAt)
            .Take(10)
            .Select(p => new
            {
                p.Title,
                City           = p.City ?? "—",
                OldPrice       = (double)p.PreviousPrice!,
                NewPrice       = (double)p.Price!,
                Currency       = p.Currency ?? "CLP",
                PriceChangedAt = p.PriceChangedAt!.Value,
            })
            .ToListAsync())
            .Select(p => new PriceChangeRow
            {
                Title          = p.Title.Length > 55 ? p.Title.Substring(0, 55) + "…" : p.Title,
                City           = p.City,
                OldPrice       = p.OldPrice,
                NewPrice       = p.NewPrice,
                Currency       = p.Currency,
                PriceChangedAt = p.PriceChangedAt,
            })
            .ToList();

        // ── Globales ──────────────────────────────────────────────────────────
        var ufProps  = await q.Where(p => p.Currency == "UF"  && p.Price > 0).Select(p => (double)p.Price!).ToListAsync();
        var clpProps = await q.Where(p => p.Currency == "CLP" && p.Price > 0).Select(p => (double)p.Price!).ToListAsync();

        double? globalAvgUF  = ufProps.Count  > 0 ? ufProps.Average()  : null;
        double? globalAvgCLP = clpProps.Count > 0 ? clpProps.Average() : null;

        // ── Tracking stats ────────────────────────────────────────────────────
        var newLast7    = await q.CountAsync(p => p.FirstSeenAt != null && p.FirstSeenAt >= DateTime.UtcNow.AddDays(-7));
        var withChanges = await q.CountAsync(p => p.PreviousPrice != null);

        // ── Distribución Nuevo/Usado ──────────────────────────────────────────
        var condDistrib = await q
            .Where(p => p.Condition != null)
            .GroupBy(p => p.Condition!)
            .Select(g => new ConditionDistribRow { Condition = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ToListAsync();

        // ── Precio promedio por estado ────────────────────────────────────────
        var priceByCondition = await q
            .Where(p => p.Condition != null && p.Price > 0)
            .GroupBy(p => new { Cond = p.Condition!, Cur = p.Currency ?? "CLP" })
            .Select(g => new PriceByConditionRow
            {
                Condition = g.Key.Cond,
                Currency  = g.Key.Cur,
                Avg       = g.Average(p => (double)p.Price!),
                Min       = g.Min(p => (double)p.Price!),
                Max       = g.Max(p => (double)p.Price!),
            })
            .ToListAsync();

        // ── Publicaciones por mes (últimos 12 meses) ──────────────────────────
        var twelveAgo = DateTime.UtcNow.AddMonths(-12);
        var pubsByMonth = await q
            .Where(p => p.FirstSeenAt != null && p.FirstSeenAt >= twelveAgo)
            .GroupBy(p => new { p.FirstSeenAt!.Value.Year, p.FirstSeenAt!.Value.Month })
            .Select(g => new MonthlyPublicationRow { Year = g.Key.Year, Month = g.Key.Month, Count = g.Count() })
            .OrderBy(g => g.Year).ThenBy(g => g.Month)
            .ToListAsync();

        // ── Propiedades por fuente (desde Bots) ───────────────────────────────
        var bySource = await _context.Bots
            .Where(b => b.Source != null)
            .GroupBy(b => b.Source!)
            .Select(g => new BotSourceRow { Source = g.Key, TotalScraped = g.Sum(b => (long)b.TotalScraped) })
            .OrderByDescending(g => g.TotalScraped)
            .Take(8)
            .ToListAsync();

        // ── Precio/m² — datos base ────────────────────────────────────────────
        var ppsqmRaw = await q
            .Where(p => p.Price > 0 && p.Area > 0)
            .Select(p => new
            {
                Type  = p.PropertyType,
                City  = p.City ?? p.Neighborhood,
                Cond  = p.Condition,
                Cur   = p.Currency ?? "CLP",
                PpSqm = (double)p.Price! / (double)p.Area!,
            })
            .ToListAsync();

        // por tipo
        var ppsqmByType = ppsqmRaw
            .Where(x => x.Type != null)
            .GroupBy(x => new { Type = x.Type!, x.Cur })
            .Select(g => new PricePerSqmRow { Name = g.Key.Type, Currency = g.Key.Cur, AvgPerSqm = g.Average(x => x.PpSqm), Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToList();

        // por ciudad top 15
        var top15Cities = ppsqmRaw
            .Where(x => x.City != null)
            .GroupBy(x => x.City!)
            .OrderByDescending(g => g.Count())
            .Take(15)
            .Select(g => g.Key)
            .ToHashSet();

        var ppsqmByCity = ppsqmRaw
            .Where(x => x.City != null && top15Cities.Contains(x.City!))
            .GroupBy(x => new { City = x.City!, x.Cur })
            .Select(g => new PricePerSqmRow { Name = g.Key.City, Currency = g.Key.Cur, AvgPerSqm = g.Average(x => x.PpSqm), Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToList();

        // por condición
        var ppsqmByCondition = ppsqmRaw
            .Where(x => x.Cond != null)
            .GroupBy(x => new { Cond = x.Cond!, x.Cur })
            .Select(g => new PricePerSqmRow { Name = g.Key.Cond, Currency = g.Key.Cur, AvgPerSqm = g.Average(x => x.PpSqm), Count = g.Count() })
            .ToList();

        // globales
        var ufPpsqmList  = ppsqmRaw.Where(x => x.Cur == "UF").Select(x => x.PpSqm).ToList();
        var clpPpsqmList = ppsqmRaw.Where(x => x.Cur == "CLP").Select(x => x.PpSqm).ToList();
        double? globalPpsqmUF  = ufPpsqmList.Count  > 0 ? ufPpsqmList.Average()  : null;
        double? globalPpsqmCLP = clpPpsqmList.Count > 0 ? clpPpsqmList.Average() : null;

        // ── Estado por tipo ────────────────────────────────────────────────────
        var condByTypeRaw = await q
            .Where(p => p.PropertyType != null && p.Condition != null)
            .GroupBy(p => new { Type = p.PropertyType!, Cond = p.Condition! })
            .Select(g => new { g.Key.Type, g.Key.Cond, Count = g.Count() })
            .ToListAsync();

        var condByType = condByTypeRaw
            .GroupBy(x => x.Type)
            .Select(g => new ConditionByTypeRow
            {
                Type  = g.Key,
                Nuevo = g.FirstOrDefault(x => x.Cond == "Nuevo")?.Count ?? 0,
                Usado = g.FirstOrDefault(x => x.Cond == "Usado")?.Count ?? 0,
            })
            .Where(x => x.Nuevo + x.Usado > 0)
            .OrderByDescending(x => x.Nuevo + x.Usado)
            .Take(8)
            .ToList();

        // ── Distribución de precios ────────────────────────────────────────────
        var priceRangesUF  = BucketPricesUF(ufProps);
        var priceRangesCLP = BucketPricesCLP(clpProps);

        // ── Evolución de precio mensual (últimos 12 meses) ─────────────────────
        var priceHistRaw = await _context.PropertySnapshots
            .Where(s => s.Price > 0 && s.ScrapedAt >= twelveAgo)
            .Join(q, s => s.PropertyId, p => p.Id,
                (s, _) => new { s.ScrapedAt.Year, s.ScrapedAt.Month, Cur = s.Currency ?? "CLP", Price = (double)s.Price! })
            .ToListAsync();

        var priceHistory = priceHistRaw
            .GroupBy(x => new { x.Year, x.Month, x.Cur })
            .Select(g => new MonthlyPriceRow
            {
                Month    = new DateTime(g.Key.Year, g.Key.Month, 1),
                Currency = g.Key.Cur,
                AvgPrice = g.Average(x => x.Price),
            })
            .OrderBy(h => h.Month)
            .ToList();

        return new ReportData
        {
            Total                  = total,
            ByType                 = byType,
            TopNeighborhoods       = topNeighborhoods,
            RecentChanges          = recentChanges,
            GlobalAvgUF            = globalAvgUF,
            GlobalAvgCLP           = globalAvgCLP,
            NewLast7Days           = newLast7,
            WithPriceChanges       = withChanges,
            GeneratedAt            = DateTime.Now,
            ConditionDistrib       = condDistrib,
            PriceByCondition       = priceByCondition,
            PublicationsByMonth    = pubsByMonth,
            BySource               = bySource,
            PricePerSqmByType      = ppsqmByType,
            PricePerSqmByCity      = ppsqmByCity,
            PricePerSqmByCondition = ppsqmByCondition,
            GlobalAvgPpsqmUF       = globalPpsqmUF,
            GlobalAvgPpsqmCLP      = globalPpsqmCLP,
            ConditionByType        = condByType,
            PriceRangesUF          = priceRangesUF,
            PriceRangesCLP         = priceRangesCLP,
            PriceHistory           = priceHistory,
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    // LATEX BUILDER — INFORME DE MERCADO
    // ══════════════════════════════════════════════════════════════════════

    private static string BuildLatexDocument(ReportData d, ReportFilters f)
    {
        var sb = new StringBuilder();

        // ── Título dinámico ───────────────────────────────────────────────────
        var scope = new List<string>();
        if (!string.IsNullOrWhiteSpace(f.Region))       scope.Add(Esc(f.Region!));
        if (!string.IsNullOrWhiteSpace(f.City))         scope.Add(Esc(f.City!));
        if (!string.IsNullOrWhiteSpace(f.Neighborhood)) scope.Add(Esc(f.Neighborhood!));
        if (!string.IsNullOrWhiteSpace(f.PropertyType)) scope.Add(Esc(f.PropertyType!));
        var scopeStr = scope.Count > 0 ? string.Join(" $\\cdot$ ", scope) : "Mercado General";

        sb.AppendLine(@"\documentclass[11pt,a4paper]{article}

% ── Encoding & idioma ──────────────────────────────────────────────────────────
\usepackage[utf8]{inputenc}
\usepackage[T1]{fontenc}
\usepackage[spanish]{babel}

% ── Tipografía ─────────────────────────────────────────────────────────────────
\usepackage{lmodern}
\usepackage{microtype}

% ── Layout ────────────────────────────────────────────────────────────────────
\usepackage[top=2.2cm,bottom=2.2cm,left=2.5cm,right=2.5cm]{geometry}
\usepackage{parskip}

% ── Colores ───────────────────────────────────────────────────────────────────
\usepackage[table]{xcolor}
\definecolor{primary}{HTML}{0F4C81}
\definecolor{accent}{HTML}{2EC4B6}
\definecolor{lightbg}{HTML}{F0F4F8}
\definecolor{muted}{HTML}{6C757D}
\definecolor{up}{HTML}{16A34A}
\definecolor{down}{HTML}{DC2626}
\definecolor{nuevo}{HTML}{2A9D8F}
\definecolor{usado}{HTML}{F4A261}

% ── Tablas ────────────────────────────────────────────────────────────────────
\usepackage{booktabs}
\usepackage{tabularx}
\usepackage{multirow}
\usepackage{array}
\newcolumntype{R}[1]{>{\raggedleft\arraybackslash}p{#1}}
\newcolumntype{C}[1]{>{\centering\arraybackslash}p{#1}}
\newcolumntype{L}[1]{>{\raggedright\arraybackslash}p{#1}}

% ── Gráficos ──────────────────────────────────────────────────────────────────
\usepackage{pgfplots}
\pgfplotsset{compat=1.18}

% ── Cabecera / pie ─────────────────────────────────────────────────────────────
\usepackage{fancyhdr}
\pagestyle{fancy}
\fancyhf{}
\renewcommand{\headrulewidth}{0.4pt}
\fancyhead[L]{\small\color{muted}InmobiScrap}
\fancyhead[R]{\small\color{muted}" + Esc(d.GeneratedAt.ToString("dd/MM/yyyy HH:mm")) + @"}
\fancyfoot[C]{\small\color{muted}\thepage}

% ── Título personalizado ───────────────────────────────────────────────────────
\usepackage{titlesec}
\titleformat{\section}{\large\bfseries\color{primary}}{}{0em}{}[\titlerule]
\titleformat{\subsection}{\normalsize\bfseries\color{primary}}{}{0em}{}

% ── Misc ───────────────────────────────────────────────────────────────────────
\usepackage{graphicx}
\usepackage{enumitem}
\usepackage{calc}

%% Comando para KPI card ────────────────────────────────────────────────────────
\newcommand{\kpicard}[3]{%
  \begin{minipage}[t]{0.30\linewidth}
    \centering
    \colorbox{lightbg}{\parbox{\linewidth-2\fboxsep}{%
      \centering\vspace{4pt}
      {\scriptsize\color{muted} #1}\\[2pt]
      {\large\bfseries\color{primary} #2}\\[2pt]
      {\scriptsize\color{muted} #3}
      \vspace{4pt}
    }}
  \end{minipage}%
}

\begin{document}

% ─────────────────────────────────────────────────────────────────────────────
%  PORTADA
% ─────────────────────────────────────────────────────────────────────────────
\begin{center}
  {\Huge\bfseries\color{primary} InmobiScrap}\\[6pt]
  {\large\color{muted} Informe de Mercado Inmobiliario}\\[4pt]
  {\normalsize\color{muted} " + scopeStr + @"}\\[8pt]
  \textcolor{muted}{\rule{\linewidth}{0.6pt}}\\[4pt]
  {\small\color{muted} Generado el " + Esc(d.GeneratedAt.ToString("dddd, dd 'de' MMMM 'de' yyyy", new CultureInfo("es-CL"))) + @"}
\end{center}

\vspace{0.5em}

% ─────────────────────────────────────────────────────────────────────────────
%  KPI CARDS
% ─────────────────────────────────────────────────────────────────────────────
\noindent
\kpicard{Total Propiedades}{" + d.Total.ToString("N0") + @"}{en la selección}%
\hfill
\kpicard{Nuevas (últ. 7 días)}{" + (d.NewLast7Days == d.Total ? "---" : "+" + d.NewLast7Days.ToString("N0")) + @"}{añadidas vs hace 7 días}%
\hfill
\kpicard{Con cambio de precio}{" + d.WithPriceChanges.ToString("N0") + @"}{detectados}

\vspace{1em}

\noindent
" + BuildGlobalAvgCards(d) + @"
");

        // Precio/m² global cards
        var ppsqmCards = BuildPricePerSqmCards(d);
        if (!string.IsNullOrEmpty(ppsqmCards))
        {
            sb.AppendLine(@"\vspace{0.8em}
\noindent
" + ppsqmCards + @"
");
        }

        sb.AppendLine(@"\vspace{1.5em}");

        // ═════════════════════════════════════════════════════════════════════
        //  SECCIÓN 1: DISTRIBUCIÓN DEL MERCADO
        // ═════════════════════════════════════════════════════════════════════
        bool hasDistrib = d.ByType.Count > 0 || d.ConditionDistrib.Count > 0 || d.BySource.Count > 0;
        if (hasDistrib)
        {
            sb.AppendLine(@"\section{Distribución del Mercado}");
            sb.AppendLine(@"\vspace{0.3em}");

            // Chart: Distribución por Tipo
            if (d.ByType.Count > 0)
            {
                var items = d.ByType.OrderByDescending(t => t.Count).Take(10)
                    .Select(t => (t.Type, (double)t.Count)).ToList();
                sb.Append(HBarChart("Distribución por Tipo de Propiedad", items, "primary!70", "Cantidad de Propiedades"));
            }

            // Chart: Nuevo vs Usado
            if (d.ConditionDistrib.Count > 0)
            {
                var items = d.ConditionDistrib.Select(c => (c.Condition, (double)c.Count)).ToList();
                sb.Append(HBarChart("Estado de Propiedades (Nuevo / Usado)", items, "nuevo!60", "Cantidad"));
            }

            // Chart: Propiedades por Fuente
            if (d.BySource.Count > 0)
            {
                var items = d.BySource.Select(s => (s.Source, (double)s.TotalScraped)).ToList();
                sb.Append(HBarChart("Propiedades Detectadas por Fuente", items, "muted!60", "Total Detectadas"));
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  SECCIÓN 2: PROMEDIOS POR TIPO
        // ═════════════════════════════════════════════════════════════════════
        sb.AppendLine(@"
\section{Promedios por Tipo de Propiedad}

\rowcolors{2}{lightbg}{white}
\begin{tabularx}{\linewidth}{L{2.6cm} C{1.2cm} C{1.0cm} C{1.0cm} C{1.0cm} C{1.0cm} R{2.4cm} R{3.0cm}}
  \toprule
  \rowcolor{primary}
  \textcolor{white}{\textbf{Tipo}} &
  \textcolor{white}{\textbf{Cant.}} &
  \textcolor{white}{\textbf{Mon.}} &
  \textcolor{white}{\textbf{Dorm.}} &
  \textcolor{white}{\textbf{Baños}} &
  \textcolor{white}{\textbf{m²}} &
  \textcolor{white}{\textbf{Precio Prom.}} &
  \textcolor{white}{\textbf{Rango}} \\
  \midrule
");

        var ufTypes  = d.ByType.Where(t => t.Currency.ToUpper() == "UF").ToList();
        var clpTypes = d.ByType.Where(t => t.Currency.ToUpper() != "UF").ToList();
        var allGroups = new List<(string label, List<TypeCurrencyRow> rows)>();
        if (ufTypes.Count  > 0) allGroups.Add(("UF",  ufTypes));
        if (clpTypes.Count > 0) allGroups.Add(("CLP", clpTypes));

        bool firstGroup = true;
        foreach (var (label, rows) in allGroups)
        {
            if (!firstGroup) sb.AppendLine(@"  \midrule");
            firstGroup = false;
            sb.AppendLine(@"  \multicolumn{8}{l}{\small\color{muted}\textit{Propiedades en " + label + @"}} \\");

            foreach (var t in rows)
            {
                var avgPriceStr = FormatPrice(t.AvgPrice, t.Currency);
                var rangeStr    = t.MinPrice.HasValue && t.MaxPrice.HasValue
                    ? $"{FormatPrice(t.MinPrice, t.Currency)} -- {FormatPrice(t.MaxPrice, t.Currency)}"
                    : "---";
                var dormStr = t.AvgBedrooms.HasValue  ? t.AvgBedrooms.Value.ToString("F1")  : "---";
                var bathStr = t.AvgBathrooms.HasValue ? t.AvgBathrooms.Value.ToString("F1") : "---";
                var areaStr = t.AvgArea.HasValue      ? $"{t.AvgArea.Value:F0} m\\textsuperscript{{2}}" : "---";

                sb.AppendLine(
                    $"  {Esc(t.Type)} & {t.Count:N0} & {Esc(t.Currency)} & {dormStr} & {bathStr} & {areaStr} & {avgPriceStr} & " +
                    @"{\small} " + rangeStr + @" \\");
            }
        }

        sb.AppendLine(@"  \bottomrule
\end{tabularx}");

        // Chart: Dormitorios y Baños por Tipo
        var typeWithRooms = d.ByType
            .Where(t => t.AvgBedrooms.HasValue || t.AvgBathrooms.HasValue)
            .Take(8).ToList();
        if (typeWithRooms.Count > 0)
        {
            sb.AppendLine(@"\vspace{0.8em}");
            var cats  = typeWithRooms.Select(t => t.Type).ToList();
            var beds  = typeWithRooms.Select(t => t.AvgBedrooms  ?? 0).ToList();
            var baths = typeWithRooms.Select(t => t.AvgBathrooms ?? 0).ToList();
            sb.Append(GroupedBar2(
                "Promedio de Dormitorios y Baños por Tipo",
                cats, beds, "Dormitorios", "primary!70",
                baths, "Baños", "nuevo!70", "Promedio"));
        }

        // ═════════════════════════════════════════════════════════════════════
        //  SECCIÓN 3: ANÁLISIS DE PRECIOS
        // ═════════════════════════════════════════════════════════════════════
        bool hasPriceCharts = d.PriceByCondition.Count > 0 || d.PriceRangesUF.Count > 0 || d.PriceRangesCLP.Count > 0;
        if (hasPriceCharts)
        {
            sb.AppendLine(@"\newpage
\section{Análisis de Precios}
\vspace{0.3em}");

            // Chart: Precio promedio por estado
            var condUF  = d.PriceByCondition.Where(x => x.Currency.ToUpper() == "UF").ToList();
            var condCLP = d.PriceByCondition.Where(x => x.Currency.ToUpper() == "CLP").ToList();

            if (condUF.Count > 0)
                sb.Append(VBarChart("Precio Promedio por Estado (UF)",
                    condUF.Select(x => (x.Condition, x.Avg)).ToList(), "primary!70", "UF", showLabels: false));

            if (condCLP.Count > 0)
                sb.Append(VBarChart("Precio Promedio por Estado (CLP)",
                    condCLP.Select(x => (x.Condition, x.Avg)).ToList(), "accent!70", "CLP", showLabels: false));

            // Chart: Distribución de precios
            if (d.PriceRangesUF.Count > 0)
                sb.Append(VBarChart("Distribución de Precios (UF)",
                    d.PriceRangesUF.Select(r => (r.Range, (double)r.Count)).ToList(), "primary!60", "Propiedades"));

            if (d.PriceRangesCLP.Count > 0)
                sb.Append(VBarChart("Distribución de Precios (CLP)",
                    d.PriceRangesCLP.Select(r => (r.Range, (double)r.Count)).ToList(), "accent!60", "Propiedades"));
        }

        // ═════════════════════════════════════════════════════════════════════
        //  SECCIÓN 4: PRECIO POR M²
        // ═════════════════════════════════════════════════════════════════════
        bool hasPpsqm = d.PricePerSqmByType.Count > 0 || d.PricePerSqmByCondition.Count > 0;
        if (hasPpsqm)
        {
            sb.AppendLine(@"\newpage
\section{Precio por m\textsuperscript{2}}
\vspace{0.3em}");

            // Chart: Precio/m² por tipo
            var ppsqmTypeUF  = d.PricePerSqmByType.Where(x => x.Currency.ToUpper() == "UF").OrderByDescending(x => x.Count).Take(8).ToList();
            var ppsqmTypeCLP = d.PricePerSqmByType.Where(x => x.Currency.ToUpper() == "CLP").OrderByDescending(x => x.Count).Take(8).ToList();

            if (ppsqmTypeUF.Count > 0)
                sb.Append(HBarChart("Precio/m² por Tipo (UF)",
                    ppsqmTypeUF.Select(x => (x.Name, x.AvgPerSqm)).ToList(), "primary!70", "UF/m²", showLabels: false));

            if (ppsqmTypeCLP.Count > 0)
                sb.Append(HBarChart("Precio/m² por Tipo (CLP)",
                    ppsqmTypeCLP.Select(x => (x.Name, x.AvgPerSqm)).ToList(), "accent!70", "CLP/m²", showLabels: false));

            // Chart: Precio/m² por estado
            var ppsqmCondUF  = d.PricePerSqmByCondition.Where(x => x.Currency.ToUpper() == "UF").ToList();
            var ppsqmCondCLP = d.PricePerSqmByCondition.Where(x => x.Currency.ToUpper() == "CLP").ToList();

            if (ppsqmCondUF.Count > 0)
                sb.Append(VBarChart("Precio/m² por Estado (UF)",
                    ppsqmCondUF.Select(x => (x.Name, x.AvgPerSqm)).ToList(), "primary!70", "UF/m²", showLabels: false));

            if (ppsqmCondCLP.Count > 0)
                sb.Append(VBarChart("Precio/m² por Estado (CLP)",
                    ppsqmCondCLP.Select(x => (x.Name, x.AvgPerSqm)).ToList(), "nuevo!70", "CLP/m²", showLabels: false));
        }

        // ═════════════════════════════════════════════════════════════════════
        //  SECCIÓN 5: ACTIVIDAD DEL MERCADO
        // ═════════════════════════════════════════════════════════════════════
        bool hasActivity = d.PublicationsByMonth.Count > 1 || d.ConditionByType.Count > 0;
        if (hasActivity)
        {
            sb.AppendLine(@"\newpage
\section{Actividad del Mercado}
\vspace{0.3em}");

            // Chart: Publicaciones por mes
            if (d.PublicationsByMonth.Count > 1)
            {
                var labels = d.PublicationsByMonth.Select(m => MonthLabel(m.Year, m.Month)).ToList();
                var counts = d.PublicationsByMonth.Select(m => (double)m.Count).ToList();
                sb.Append(LineArea("Publicaciones por Mes (últimos 12 meses)", labels, counts, "primary", "Propiedades"));
            }

            // Chart: Estado por tipo
            if (d.ConditionByType.Count > 0)
            {
                sb.AppendLine(@"\vspace{0.5em}");
                var cats  = d.ConditionByType.Select(x => x.Type).ToList();
                var nuevo = d.ConditionByType.Select(x => (double)x.Nuevo).ToList();
                var usado = d.ConditionByType.Select(x => (double)x.Usado).ToList();
                sb.Append(GroupedBar2(
                    "Estado por Tipo de Propiedad",
                    cats, nuevo, "Nuevo", "nuevo!70",
                    usado, "Usado", "usado!70", "Cantidad"));
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  SECCIÓN 6: EVOLUCIÓN DE PRECIO DEL MERCADO
        // ═════════════════════════════════════════════════════════════════════
        var histUF  = d.PriceHistory.Where(h => h.Currency.ToUpper() == "UF").OrderBy(h => h.Month).ToList();
        var histCLP = d.PriceHistory.Where(h => h.Currency.ToUpper() == "CLP").OrderBy(h => h.Month).ToList();

        if (histUF.Count > 1 || histCLP.Count > 1)
        {
            sb.AppendLine(@"\newpage
\section{Evolución de Precio del Mercado}
\vspace{0.3em}");

            if (histUF.Count > 1)
            {
                var labels = histUF.Select(h => MonthLabel(h.Month.Year, h.Month.Month)).ToList();
                var values = histUF.Select(h => h.AvgPrice).ToList();
                sb.Append(LineArea("Precio Promedio Mensual (UF) --- últimos 12 meses", labels, values, "primary", "UF"));
            }

            if (histCLP.Count > 1)
            {
                var labels = histCLP.Select(h => MonthLabel(h.Month.Year, h.Month.Month)).ToList();
                var values = histCLP.Select(h => h.AvgPrice).ToList();
                sb.Append(LineArea("Precio Promedio Mensual (CLP) --- últimos 12 meses", labels, values, "accent", "CLP"));
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  SECCIÓN 7: TOP COMUNAS / CIUDADES
        // ═════════════════════════════════════════════════════════════════════
        sb.AppendLine(@"
\newpage
\section{Top Comunas / Ciudades por Cantidad de Propiedades}

\rowcolors{2}{lightbg}{white}
\begin{tabularx}{\linewidth}{L{4.5cm} C{2cm} R{3cm} R{3cm}}
  \toprule
  \rowcolor{primary}
  \textcolor{white}{\textbf{Comuna / Ciudad}} &
  \textcolor{white}{\textbf{Propiedades}} &
  \textcolor{white}{\textbf{Precio Prom. (UF)}} &
  \textcolor{white}{\textbf{Precio Prom. (CLP)}} \\
  \midrule
");

        foreach (var n in d.TopNeighborhoods)
        {
            var ufStr  = n.AvgPriceUF.HasValue  ? $"{n.AvgPriceUF.Value:N0} UF"            : "---";
            var clpStr = n.AvgPriceCLP.HasValue ? FormatPrice(n.AvgPriceCLP.Value, "CLP") : "---";
            sb.AppendLine($"  {Esc(n.Name)} & {n.Count:N0} & {ufStr} & {clpStr} \\\\");
        }

        sb.AppendLine(@"  \bottomrule
\end{tabularx}");

        // Chart: Precio/m² por ciudad
        var cityUF  = d.PricePerSqmByCity.Where(x => x.Currency.ToUpper() == "UF").OrderByDescending(x => x.Count).Take(10).ToList();
        var cityCLP = d.PricePerSqmByCity.Where(x => x.Currency.ToUpper() == "CLP").OrderByDescending(x => x.Count).Take(10).ToList();

        if (cityUF.Count > 0 || cityCLP.Count > 0)
        {
            sb.AppendLine(@"\vspace{0.8em}
\subsection{Precio por m\textsuperscript{2} por Ciudad}");

            if (cityUF.Count > 0)
                sb.Append(HBarChart("Precio/m² por Ciudad (UF) --- Top " + cityUF.Count,
                    cityUF.Select(x => (x.Name, x.AvgPerSqm)).ToList(), "primary!70", "UF/m²", showLabels: false));

            if (cityCLP.Count > 0)
                sb.Append(HBarChart("Precio/m² por Ciudad (CLP) --- Top " + cityCLP.Count,
                    cityCLP.Select(x => (x.Name, x.AvgPerSqm)).ToList(), "accent!70", "CLP/m²", showLabels: false));
        }

        // ═════════════════════════════════════════════════════════════════════
        //  SECCIÓN 8: CAMBIOS DE PRECIO RECIENTES
        // ═════════════════════════════════════════════════════════════════════
        if (d.RecentChanges.Count > 0)
        {
            sb.AppendLine(@"
\newpage
\section{Cambios de Precio Recientes}

\rowcolors{2}{lightbg}{white}
\begin{tabularx}{\linewidth}{L{5.5cm} C{1.8cm} R{2.3cm} R{2.3cm} R{2.3cm}}
  \toprule
  \rowcolor{primary}
  \textcolor{white}{\textbf{Propiedad}} &
  \textcolor{white}{\textbf{Ciudad}} &
  \textcolor{white}{\textbf{Precio Ant.}} &
  \textcolor{white}{\textbf{Precio Act.}} &
  \textcolor{white}{\textbf{Variación}} \\
  \midrule
");
            foreach (var c in d.RecentChanges)
            {
                var diff    = c.NewPrice - c.OldPrice;
                var diffPct = c.OldPrice > 0 ? diff / c.OldPrice * 100 : 0;
                var color   = diff >= 0 ? "up" : "down";
                var sign    = diff >= 0 ? "+" : "";
                var varStr  = $@"\textcolor{{{color}}}{{{sign}{diffPct:F1}\%}}";
                sb.AppendLine(
                    $"  {Esc(c.Title)} & {Esc(c.City)} & {FormatPrice(c.OldPrice, c.Currency)} & " +
                    $"{FormatPrice(c.NewPrice, c.Currency)} & {varStr} \\\\");
            }
            sb.AppendLine(@"  \bottomrule
\end{tabularx}");
        }

        // ── Nota al pie ───────────────────────────────────────────────────────
        sb.AppendLine(@"
\vfill
\textcolor{muted}{\rule{\linewidth}{0.4pt}}\\
{\scriptsize\color{muted}
  Este informe fue generado automáticamente por \textbf{InmobiScrap}.
  Los datos provienen de scraping de portales inmobiliarios chilenos y pueden contener imprecisiones.
  Todos los precios en UF y CLP corresponden a la moneda indicada por la fuente original.
}

\end{document}");

        return sb.ToString();
    }

    // ══════════════════════════════════════════════════════════════════════
    // LATEX HELPERS — GRÁFICOS
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Gráfico de barras horizontal (xbar). items: orden descendente → el primero aparece arriba.</summary>
    private static string HBarChart(
        string title,
        List<(string Label, double Value)> items,
        string color        = "primary!70",
        string xlabel       = "Cantidad",
        bool   showLabels   = true)
    {
        if (items.Count == 0) return "";
        // Invert so first item (highest) appears at the top of the chart
        var ordered = items.AsEnumerable().Reverse().ToList();
        double h    = Math.Max(4.0, ordered.Count * 0.65);
        double xmax = Math.Ceiling(ordered.Max(x => x.Value) * 1.20);

        var syms = string.Join(",", ordered.Select((_, i) => $"L{i}"));
        var labs = string.Join(",", ordered.Select(x =>
        {
            var l = x.Label.Length > 25 ? x.Label[..25] + "." : x.Label;
            return "{" + Esc(l) + "}";   // braces required for labels with spaces in pgfplots
        }));
        var pts = string.Join(" ", ordered.Select((x, i) =>
            $"({x.Value.ToString("F0", CultureInfo.InvariantCulture)},L{i})"));

        var nearCoordsBlock = showLabels
            ? @"    nodes near coords,
    nodes near coords style={font=\tiny, anchor=west},"
            : "    % no node labels,";

        return $@"
\begin{{center}}
\begin{{tikzpicture}}
\begin{{axis}}[
    title={{\textbf{{{Esc(title)}}}}},
    xbar,
    symbolic y coords={{{syms}}},
    ytick=data,
    yticklabels={{{labs}}},
    xlabel={{{Esc(xlabel)}}},
    xlabel style={{font=\small}},
    y tick label style={{font=\small}},
    width=0.92\linewidth,
    height={h.ToString("F1", CultureInfo.InvariantCulture)}cm,
    xmin=0, xmax={xmax.ToString("F0", CultureInfo.InvariantCulture)},
    bar width=0.38cm,
{nearCoordsBlock}
    enlarge y limits=0.12,
    grid=major, grid style={{dashed, gray!30}},
]
\addplot[fill={color}] coordinates {{{pts}}};
\end{{axis}}
\end{{tikzpicture}}
\end{{center}}
";
    }

    /// <summary>Gráfico de barras vertical (ybar), una serie.</summary>
    private static string VBarChart(
        string title,
        List<(string Label, double Value)> items,
        string color      = "primary!70",
        string ylabel     = "Valor",
        bool   showLabels = true)
    {
        if (items.Count == 0) return "";
        double ymax = Math.Ceiling(items.Max(x => x.Value) * 1.20);
        if (ymax == 0) ymax = 1;

        var syms = string.Join(",", items.Select((_, i) => $"T{i}"));
        var labs = string.Join(",", items.Select(x =>
        {
            var l = x.Label.Length > 14 ? x.Label[..14] + "." : x.Label;
            return "{" + Esc(l) + "}";   // braces required for labels with spaces in pgfplots
        }));
        var pts = string.Join(" ", items.Select((x, i) =>
            $"(T{i},{x.Value.ToString("F0", CultureInfo.InvariantCulture)})"));

        var nearCoordsBlock = showLabels
            ? @"    nodes near coords,
    nodes near coords style={font=\tiny, rotate=90, anchor=west},"
            : "    % no node labels,";

        return $@"
\begin{{center}}
\begin{{tikzpicture}}
\begin{{axis}}[
    title={{\textbf{{{Esc(title)}}}}},
    ybar,
    symbolic x coords={{{syms}}},
    xtick=data,
    xticklabels={{{labs}}},
    x tick label style={{rotate=40, anchor=east, font=\small}},
    ylabel={{{Esc(ylabel)}}},
    ylabel style={{font=\small}},
    y tick label style={{font=\small, /pgf/number format/1000 sep={{\,}}}},
    width=0.92\linewidth,
    height=6.5cm,
    ymin=0, ymax={ymax.ToString("F0", CultureInfo.InvariantCulture)},
    bar width=0.5cm,
{nearCoordsBlock}
    enlarge x limits=0.15,
    grid=major, grid style={{dashed, gray!30}},
]
\addplot[fill={color}] coordinates {{{pts}}};
\end{{axis}}
\end{{tikzpicture}}
\end{{center}}
";
    }

    /// <summary>Gráfico de barras agrupadas vertical (2 series).</summary>
    private static string GroupedBar2(
        string title,
        List<string> categories,
        List<double> vals1, string lbl1, string col1,
        List<double> vals2, string lbl2, string col2,
        string ylabel = "Valor")
    {
        if (categories.Count == 0) return "";
        int n = categories.Count;

        double allMax = 0;
        if (vals1.Count > 0) allMax = Math.Max(allMax, vals1.Max());
        if (vals2.Count > 0) allMax = Math.Max(allMax, vals2.Max());
        double ymax = Math.Ceiling(allMax * 1.20);
        if (ymax == 0) ymax = 1;

        var syms = string.Join(",", categories.Select((_, i) => $"T{i}"));
        var labs = string.Join(",", categories.Select(c =>
        {
            var l = c.Length > 14 ? c[..14] + "." : c;
            return "{" + Esc(l) + "}";   // braces required for labels with spaces in pgfplots
        }));

        string Pts(List<double> vs) =>
            string.Join(" ", vs.Select((v, i) =>
                $"(T{i},{v.ToString("F0", CultureInfo.InvariantCulture)})"));

        return $@"
\begin{{center}}
\begin{{tikzpicture}}
\begin{{axis}}[
    title={{\textbf{{{Esc(title)}}}}},
    ybar,
    symbolic x coords={{{syms}}},
    xtick=data,
    xticklabels={{{labs}}},
    x tick label style={{rotate=40, anchor=east, font=\small}},
    ylabel={{{Esc(ylabel)}}},
    ylabel style={{font=\small}},
    y tick label style={{font=\small, /pgf/number format/1000 sep={{\,}}}},
    width=0.92\linewidth,
    height=7cm,
    ymin=0, ymax={ymax.ToString("F0", CultureInfo.InvariantCulture)},
    bar width=0.28cm,
    nodes near coords,
    nodes near coords style={{font=\tiny}},
    enlarge x limits=0.12,
    legend style={{at={{(0.5,-0.26)}}, anchor=north, legend columns=2, font=\small}},
    grid=major, grid style={{dashed, gray!30}},
]
\addplot[fill={col1}] coordinates {{{Pts(vals1)}}};
\addlegendentry{{{Esc(lbl1)}}}
\addplot[fill={col2}] coordinates {{{Pts(vals2)}}};
\addlegendentry{{{Esc(lbl2)}}}
\end{{axis}}
\end{{tikzpicture}}
\end{{center}}
";
    }

    /// <summary>Gráfico de línea con área rellena (series temporal).</summary>
    private static string LineArea(
        string title,
        List<string> xlabels,
        List<double> values,
        string color  = "primary",
        string ylabel = "Valor")
    {
        if (xlabels.Count == 0 || values.Count == 0) return "";
        int n = Math.Min(xlabels.Count, values.Count);
        if (n < 2) return "";

        double ymax = Math.Ceiling(values.Take(n).Max() * 1.15);
        if (ymax == 0) ymax = 1;

        // Ticks: show at most 12, evenly spaced
        int step = Math.Max(1, (int)Math.Ceiling(n / 12.0));
        var ticks     = new List<int>();
        var tickLabs  = new List<string>();
        for (int i = 0; i < n; i += step) { ticks.Add(i); tickLabs.Add(Esc(xlabels[i])); }
        if (!ticks.Contains(n - 1)) { ticks.Add(n - 1); tickLabs.Add(Esc(xlabels[n - 1])); }

        var pts = string.Join(" ", Enumerable.Range(0, n).Select(i =>
            $"({i},{values[i].ToString("F0", CultureInfo.InvariantCulture)})"));

        return $@"
\begin{{center}}
\begin{{tikzpicture}}
\begin{{axis}}[
    title={{\textbf{{{Esc(title)}}}}},
    width=0.95\linewidth,
    height=6.5cm,
    xmin=0, xmax={n - 1},
    ymin=0, ymax={ymax.ToString("F0", CultureInfo.InvariantCulture)},
    xtick={{{string.Join(",", ticks)}}},
    xticklabels={{{string.Join(",", tickLabs)}}},
    x tick label style={{rotate=45, anchor=east, font=\small}},
    ylabel={{{Esc(ylabel)}}},
    ylabel style={{font=\small}},
    y tick label style={{font=\small, /pgf/number format/1000 sep={{\,}}}},
    enlarge x limits=0.04,
    grid=major, grid style={{dashed, gray!30}},
    mark options={{solid}},
]
\addplot[fill={color}!25, draw={color}, thick, mark=*, mark size=1.8pt] coordinates {{{pts}}};
\end{{axis}}
\end{{tikzpicture}}
\end{{center}}
";
    }

    // ══════════════════════════════════════════════════════════════════════
    // LATEX BUILDER — INFORME COMPARATIVO (sin cambios)
    // ══════════════════════════════════════════════════════════════════════

    private static string BuildComparisonLatex(List<Inmobiscrap.Models.Property> properties, List<Inmobiscrap.Models.PropertySnapshot> snapshots)
    {
        var sb = new StringBuilder();
        var now = DateTime.Now;

        sb.AppendLine(@"\documentclass[11pt,a4paper]{article}

% ── Encoding & idioma ──────────────────────────────────────────────────────────
\usepackage[utf8]{inputenc}
\usepackage[T1]{fontenc}
\usepackage[spanish]{babel}

% ── Tipografía ─────────────────────────────────────────────────────────────────
\usepackage{lmodern}
\usepackage{microtype}

% ── Layout ────────────────────────────────────────────────────────────────────
\usepackage[landscape,top=1.8cm,bottom=1.8cm,left=1.5cm,right=1.5cm]{geometry}
\usepackage{parskip}

% ── Colores ───────────────────────────────────────────────────────────────────
\usepackage[table]{xcolor}
\definecolor{primary}{HTML}{0F4C81}
\definecolor{accent}{HTML}{2EC4B6}
\definecolor{lightbg}{HTML}{F0F4F8}
\definecolor{muted}{HTML}{6C757D}
\definecolor{up}{HTML}{16A34A}
\definecolor{down}{HTML}{DC2626}
\definecolor{nuevo}{HTML}{2A9D8F}
\definecolor{usado}{HTML}{F4A261}

% ── Tablas ────────────────────────────────────────────────────────────────────
\usepackage{booktabs}
\usepackage{tabularx}
\usepackage{array}
\usepackage{longtable}
\newcolumntype{R}[1]{>{\raggedleft\arraybackslash}p{#1}}
\newcolumntype{C}[1]{>{\centering\arraybackslash}p{#1}}
\newcolumntype{L}[1]{>{\raggedright\arraybackslash}p{#1}}

% ── Gráficos ─────────────────────────────────────────────────────────────────
\usepackage{pgfplots}
\pgfplotsset{compat=1.18}

% ── Cabecera / pie ─────────────────────────────────────────────────────────────
\usepackage{fancyhdr}
\pagestyle{fancy}
\fancyhf{}
\renewcommand{\headrulewidth}{0.4pt}
\fancyhead[L]{\small\color{muted}InmobiScrap --- Comparativa}
\fancyhead[R]{\small\color{muted}" + Esc(now.ToString("dd/MM/yyyy HH:mm")) + @"}
\fancyfoot[C]{\small\color{muted}\thepage}

% ── Título personalizado ───────────────────────────────────────────────────────
\usepackage{titlesec}
\titleformat{\section}{\large\bfseries\color{primary}}{}{0em}{}[\titlerule]
\titleformat{\subsection}{\normalsize\bfseries\color{primary}}{}{0em}{}

\usepackage{graphicx}
\usepackage{calc}
\usepackage{enumitem}

\begin{document}

% ─────────────────────────────────────────────────────────────────────────────
%  PORTADA
% ─────────────────────────────────────────────────────────────────────────────
\begin{center}
  {\Huge\bfseries\color{primary} InmobiScrap}\\[6pt]
  {\large\color{muted} Informe Comparativo de Propiedades}\\[4pt]
  {\normalsize\color{muted} " + properties.Count + @" propiedades seleccionadas}\\[8pt]
  \textcolor{muted}{\rule{\linewidth}{0.6pt}}\\[4pt]
  {\small\color{muted} Generado el " + Esc(now.ToString("dddd, dd 'de' MMMM 'de' yyyy", new CultureInfo("es-CL"))) + @"}
\end{center}

\vspace{1em}

% ─────────────────────────────────────────────────────────────────────────────
%  TABLAS COMPARATIVAS
% ─────────────────────────────────────────────────────────────────────────────
\section{Comparación de Propiedades}
");

        // Split into chunks of up to 5 properties per table
        const int chunkSize = 5;
        var chunks = new List<List<Inmobiscrap.Models.Property>>();
        for (int c = 0; c < properties.Count; c += chunkSize)
            chunks.Add(properties.Skip(c).Take(chunkSize).ToList());

        for (int ci = 0; ci < chunks.Count; ci++)
        {
            var chunk    = chunks[ci];
            var startIdx = ci * chunkSize;

            if (ci > 0) sb.AppendLine(@"\vspace{1em}");
            if (chunks.Count > 1)
                sb.AppendLine($@"\subsection{{Propiedades {startIdx + 1} a {startIdx + chunk.Count}}}");

            var colWidth = Math.Max(3.0, 22.0 / chunk.Count);
            var colSpec  = string.Join(" ", chunk.Select(_ =>
                "C{" + colWidth.ToString("F1", CultureInfo.InvariantCulture) + "cm}"));

            sb.AppendLine($@"
\rowcolors{{2}}{{lightbg}}{{white}}
\begin{{tabularx}}{{\linewidth}}{{L{{3.2cm}} {colSpec}}}
  \toprule
  \rowcolor{{primary}}
  \textcolor{{white}}{{\textbf{{Atributo}}}}");

            for (int i = 0; i < chunk.Count; i++)
                sb.Append($" & \\textcolor{{white}}{{\\textbf{{P{startIdx + i + 1}}}}}");
            sb.AppendLine(@" \\
  \midrule");

            // Title
            sb.Append("  \\textbf{Título}");
            foreach (var p in chunk)
            {
                var maxLen = chunk.Count <= 3 ? 40 : 25;
                var title  = (p.Title ?? "---").Length > maxLen ? p.Title!.Substring(0, maxLen) + "..." : (p.Title ?? "---");
                sb.Append($" & {{\\small {Esc(title)}}}");
            }
            sb.AppendLine(" \\\\");

            // Price
            sb.Append("  \\textbf{Precio}");
            foreach (var p in chunk)
                sb.Append($" & \\textbf{{{FormatPrice(p.Price.HasValue ? (double)p.Price.Value : (double?)null, p.Currency)}}}");
            sb.AppendLine(" \\\\");

            // Type
            sb.Append("  \\textbf{Tipo}");
            foreach (var p in chunk) sb.Append($" & {Esc(p.PropertyType)}");
            sb.AppendLine(" \\\\");

            // Condition
            sb.Append("  \\textbf{Estado}");
            foreach (var p in chunk)
            {
                var cond  = p.Condition ?? "---";
                var clr   = cond == "Nuevo" ? "nuevo" : cond == "Usado" ? "usado" : "muted";
                sb.Append($" & \\textcolor{{{clr}}}{{{Esc(cond)}}}");
            }
            sb.AppendLine(" \\\\");

            // Bedrooms
            sb.Append("  \\textbf{Dormitorios}");
            foreach (var p in chunk) sb.Append($" & {(p.Bedrooms.HasValue ? p.Bedrooms.Value.ToString() : "---")}");
            sb.AppendLine(" \\\\");

            // Bathrooms
            sb.Append("  \\textbf{Baños}");
            foreach (var p in chunk) sb.Append($" & {(p.Bathrooms.HasValue ? p.Bathrooms.Value.ToString() : "---")}");
            sb.AppendLine(" \\\\");

            // Area
            sb.Append("  \\textbf{Superficie}");
            foreach (var p in chunk) sb.Append($" & {(p.Area.HasValue ? $"{p.Area.Value:F0} m\\textsuperscript{{2}}" : "---")}");
            sb.AppendLine(" \\\\");

            // Price per sqm
            sb.Append("  \\textbf{Precio/m\\textsuperscript{2}}");
            foreach (var p in chunk)
            {
                if (p.Price.HasValue && p.Price > 0 && p.Area.HasValue && p.Area > 0)
                    sb.Append($" & \\textbf{{{Math.Round(p.Price.Value / p.Area.Value, 0):N0} {Esc(p.Currency ?? "CLP")}}}");
                else
                    sb.Append(" & ---");
            }
            sb.AppendLine(" \\\\");

            // City
            sb.Append("  \\textbf{Ciudad}");
            foreach (var p in chunk) sb.Append($" & {Esc(p.City)}");
            sb.AppendLine(" \\\\");

            // Neighborhood
            sb.Append("  \\textbf{Comuna}");
            foreach (var p in chunk) sb.Append($" & {Esc(p.Neighborhood)}");
            sb.AppendLine(" \\\\");

            // Publication Date
            sb.Append("  \\textbf{Publicación}");
            foreach (var p in chunk)
                sb.Append($" & {(p.PublicationDate.HasValue ? p.PublicationDate.Value.ToString("dd/MM/yyyy") : "---")}");
            sb.AppendLine(" \\\\");

            // Days on market
            sb.Append("  \\textbf{Días en mercado}");
            foreach (var p in chunk)
            {
                if (p.PublicationDate.HasValue)
                    sb.Append($" & {(int)(DateTime.UtcNow - p.PublicationDate.Value).TotalDays}");
                else if (p.FirstSeenAt.HasValue)
                    sb.Append($@" & {(int)(DateTime.UtcNow - p.FirstSeenAt.Value).TotalDays} \textcolor{{muted}}{{\tiny (est.)}}");
                else
                    sb.Append(" & ---");
            }
            sb.AppendLine(" \\\\");

            // First seen
            sb.Append("  \\textbf{Detectada}");
            foreach (var p in chunk)
                sb.Append($" & {(p.FirstSeenAt.HasValue ? p.FirstSeenAt.Value.ToString("dd/MM/yyyy") : "---")}");
            sb.AppendLine(" \\\\");

            // Times scraped
            sb.Append("  \\textbf{Veces detectada}");
            foreach (var p in chunk) sb.Append($" & {p.TimesScraped}");
            sb.AppendLine(" \\\\");

            // Status
            sb.Append("  \\textbf{Estado listado}");
            foreach (var p in chunk) sb.Append($" & {Esc(p.ListingStatus ?? "---")}");
            sb.AppendLine(" \\\\");

            sb.AppendLine(@"  \bottomrule
\end{tabularx}");
        }

        sb.AppendLine(@"\vspace{1.5em}");

        // ══════════════════════════════════════════════════════════
        // GRÁFICOS COMPARATIVOS
        // ══════════════════════════════════════════════════════════
        sb.AppendLine(@"\newpage
\section{Gráficos Comparativos}");

        // ── Chart 1: Price comparison ──────────────────────────────
        var pricedForChart = properties.Where(p => p.Price.HasValue && p.Price > 0).ToList();
        if (pricedForChart.Count >= 2)
        {
            var currencyGroups = pricedForChart.GroupBy(p => (p.Currency ?? "CLP").ToUpper()).ToList();
            foreach (var cg in currencyGroups)
            {
                var items  = cg.ToList();
                var maxPrice = items.Max(p => (double)p.Price!);
                var yMax   = Math.Ceiling(maxPrice * 1.15);

                sb.AppendLine($@"
\vspace{{0.5em}}
\begin{{center}}
\begin{{tikzpicture}}
\begin{{axis}}[
    title={{\textbf{{Precio ({cg.Key})}}}},
    ybar,
    bar width=0.5cm,
    width=0.9\linewidth,
    height=8cm,
    ymin=0, ymax={yMax.ToString("F0", CultureInfo.InvariantCulture)},
    symbolic x coords={{{string.Join(",", items.Select((_, i) => $"P{properties.IndexOf(_) + 1}"))}}},
    xtick=data,
    x tick label style={{rotate=45, anchor=east, font=\small}},
    ylabel={{{cg.Key}}},
    ylabel style={{font=\small}},
    y tick label style={{font=\small, /pgf/number format/1000 sep={{\,}}}},
    nodes near coords,
    nodes near coords style={{font=\tiny, rotate=90, anchor=west}},
    every node near coord/.append style={{/pgf/number format/1000 sep={{\,}}}},
    enlarge x limits=0.05,
    grid=major,
    grid style={{dashed, gray!30}},
]
\addplot[fill=primary!80] coordinates {{");
                foreach (var p in items)
                    sb.Append($"(P{properties.IndexOf(p) + 1},{(double)p.Price!:F0}) ");
                sb.AppendLine(@"};
\end{axis}
\end{tikzpicture}
\end{center}");
            }
        }

        // ── Chart 2: Price per m² ──────────────────────────────────
        var withPricePerSqm = properties.Where(p => p.Price > 0 && p.Area > 0).ToList();
        if (withPricePerSqm.Count >= 2)
        {
            var currencyGroups2 = withPricePerSqm.GroupBy(p => (p.Currency ?? "CLP").ToUpper()).ToList();
            foreach (var cg in currencyGroups2)
            {
                var items       = cg.ToList();
                var ppsqmValues = items.Select(p => (double)(p.Price!.Value / p.Area!.Value)).ToList();
                var yMax        = Math.Ceiling(ppsqmValues.Max() * 1.15);

                sb.AppendLine($@"
\vspace{{0.5em}}
\begin{{center}}
\begin{{tikzpicture}}
\begin{{axis}}[
    title={{\textbf{{Precio/m\textsuperscript{{2}} ({cg.Key})}}}},
    ybar,
    bar width=0.5cm,
    width=0.9\linewidth,
    height=8cm,
    ymin=0, ymax={yMax.ToString("F0", CultureInfo.InvariantCulture)},
    symbolic x coords={{{string.Join(",", items.Select(p => $"P{properties.IndexOf(p) + 1}"))}}},
    xtick=data,
    x tick label style={{rotate=45, anchor=east, font=\small}},
    ylabel={{{cg.Key}/m\textsuperscript{{2}}}},
    ylabel style={{font=\small}},
    y tick label style={{font=\small, /pgf/number format/1000 sep={{\,}}}},
    nodes near coords,
    nodes near coords style={{font=\tiny, rotate=90, anchor=west}},
    every node near coord/.append style={{/pgf/number format/1000 sep={{\,}}}},
    enlarge x limits=0.05,
    grid=major,
    grid style={{dashed, gray!30}},
]
\addplot[fill=accent!80] coordinates {{");
                foreach (var p in items)
                {
                    var ppsqm = (double)(p.Price!.Value / p.Area!.Value);
                    sb.Append($"(P{properties.IndexOf(p) + 1},{ppsqm:F0}) ");
                }
                sb.AppendLine(@"};
\end{axis}
\end{tikzpicture}
\end{center}");
            }
        }

        // ── Chart 3: Surface area ──────────────────────────────────
        var withArea = properties.Where(p => p.Area.HasValue && p.Area > 0).ToList();
        if (withArea.Count >= 2)
        {
            var yMaxArea = Math.Ceiling((double)withArea.Max(p => p.Area!.Value) * 1.15);
            sb.AppendLine($@"
\vspace{{0.5em}}
\begin{{center}}
\begin{{tikzpicture}}
\begin{{axis}}[
    title={{\textbf{{Superficie (m\textsuperscript{{2}})}}}},
    ybar,
    bar width=0.5cm,
    width=0.9\linewidth,
    height=8cm,
    ymin=0, ymax={yMaxArea.ToString("F0", CultureInfo.InvariantCulture)},
    symbolic x coords={{{string.Join(",", withArea.Select(p => $"P{properties.IndexOf(p) + 1}"))}}},
    xtick=data,
    x tick label style={{rotate=45, anchor=east, font=\small}},
    ylabel={{m\textsuperscript{{2}}}},
    ylabel style={{font=\small}},
    y tick label style={{font=\small}},
    nodes near coords,
    nodes near coords style={{font=\tiny, rotate=90, anchor=west}},
    enlarge x limits=0.05,
    grid=major,
    grid style={{dashed, gray!30}},
]
\addplot[fill=usado!80] coordinates {{");
            foreach (var p in withArea)
                sb.Append($"(P{properties.IndexOf(p) + 1},{(double)p.Area!:F0}) ");
            sb.AppendLine(@"};
\end{axis}
\end{tikzpicture}
\end{center}");
        }

        // ── Chart 4: Bedrooms + Bathrooms grouped bar ──────────────
        var withRooms = properties.Where(p => p.Bedrooms.HasValue || p.Bathrooms.HasValue).ToList();
        if (withRooms.Count >= 2)
        {
            var maxRooms = Math.Max(
                withRooms.Max(p => p.Bedrooms  ?? 0),
                withRooms.Max(p => p.Bathrooms ?? 0));

            sb.AppendLine($@"
\vspace{{0.5em}}
\begin{{center}}
\begin{{tikzpicture}}
\begin{{axis}}[
    title={{\textbf{{Dormitorios y Baños}}}},
    ybar,
    bar width=0.3cm,
    width=0.9\linewidth,
    height=7cm,
    ymin=0, ymax={maxRooms + 2},
    symbolic x coords={{{string.Join(",", withRooms.Select(p => $"P{properties.IndexOf(p) + 1}"))}}},
    xtick=data,
    x tick label style={{rotate=45, anchor=east, font=\small}},
    ylabel={{Cantidad}},
    ylabel style={{font=\small}},
    y tick label style={{font=\small}},
    nodes near coords,
    nodes near coords style={{font=\tiny}},
    enlarge x limits=0.05,
    legend style={{at={{(0.5,-0.2)}}, anchor=north, legend columns=2, font=\small}},
    grid=major,
    grid style={{dashed, gray!30}},
]
\addplot[fill=primary!70] coordinates {{");
            foreach (var p in withRooms)
                sb.Append($"(P{properties.IndexOf(p) + 1},{p.Bedrooms ?? 0}) ");
            sb.AppendLine(@"};
\addlegendentry{Dormitorios}");
            sb.Append(@"\addplot[fill=nuevo!70] coordinates {");
            foreach (var p in withRooms)
                sb.Append($"(P{properties.IndexOf(p) + 1},{p.Bathrooms ?? 0}) ");
            sb.AppendLine(@"};
\addlegendentry{Baños}
\end{axis}
\end{tikzpicture}
\end{center}");
        }

        // ══════════════════════════════════════════════════════════
        // EVOLUCIÓN DE PRECIO EN EL TIEMPO
        // ══════════════════════════════════════════════════════════
        var snapsByProp = snapshots
            .GroupBy(s => s.PropertyId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .GroupBy(s => s.ScrapedAt.Date)
                    .Select(dg => (
                        Date:     dg.Key,
                        Price:    (double)dg.OrderByDescending(s => s.ScrapedAt).First().Price!,
                        Currency: dg.OrderByDescending(s => s.ScrapedAt).First().Currency ?? "CLP"
                    ))
                    .OrderBy(t => t.Date)
                    .ToList()
            );

        var propsWithHistory = properties
            .Where(p => snapsByProp.ContainsKey(p.Id) && snapsByProp[p.Id].Count >= 1)
            .ToList();

        if (propsWithHistory.Count >= 1)
        {
            sb.AppendLine(@"\newpage
\section{Evolución de Precio en el Tiempo}
");
            var allSnaps          = propsWithHistory.SelectMany(p => snapsByProp[p.Id]).ToList();
            var dominantCurrency  = allSnaps.GroupBy(s => s.Currency.ToUpper())
                .OrderByDescending(g => g.Count()).First().Key;

            var filteredByProp = propsWithHistory
                .Select(p => (
                    Prop:   p,
                    Points: snapsByProp[p.Id]
                        .Where(s => s.Currency.ToUpper() == dominantCurrency)
                        .ToList()
                ))
                .Where(x => x.Points.Count >= 1)
                .ToList();

            if (filteredByProp.Count >= 1)
            {
                var allDates = filteredByProp.SelectMany(x => x.Points.Select(p => p.Date)).Distinct().OrderBy(d => d).ToList();
                var minDate  = allDates.First();
                var maxDate  = allDates.Last();
                var spanDays = Math.Max(1, (maxDate - minDate).TotalDays);

                var allPrices = filteredByProp.SelectMany(x => x.Points.Select(p => p.Price)).ToList();
                var yMin = Math.Floor(allPrices.Min() * 0.97);
                var yMax = Math.Ceiling(allPrices.Max() * 1.03);

                var tickDates   = allDates.Count <= 10 ? allDates
                                : Enumerable.Range(0, 10).Select(i => allDates[(int)Math.Round(i * (allDates.Count - 1) / 9.0)]).Distinct().ToList();
                var xtick       = string.Join(",", tickDates.Select(d => ((d - minDate).TotalDays).ToString("F0", CultureInfo.InvariantCulture)));
                var xticklabels = string.Join(",", tickDates.Select(d => d.ToString("dd/MM/yy")));
                var lineColors  = new[] { "primary", "accent", "down", "usado", "nuevo", "muted" };

                sb.AppendLine($@"
\begin{{center}}
\begin{{tikzpicture}}
\begin{{axis}}[
    title={{\textbf{{Evolución de Precio ({dominantCurrency})}}}},
    xlabel={{Fecha}},
    ylabel={{{dominantCurrency}}},
    width=0.95\linewidth,
    height=9cm,
    xmin=0, xmax={spanDays.ToString("F0", CultureInfo.InvariantCulture)},
    ymin={yMin.ToString("F0", CultureInfo.InvariantCulture)},
    ymax={yMax.ToString("F0", CultureInfo.InvariantCulture)},
    xtick={{{xtick}}},
    xticklabels={{{xticklabels}}},
    x tick label style={{rotate=45, anchor=east, font=\tiny}},
    ylabel style={{font=\small}},
    y tick label style={{font=\small, /pgf/number format/1000 sep={{\,}}}},
    legend style={{at={{(0.5,-0.22)}}, anchor=north, legend columns=3, font=\scriptsize}},
    grid=major,
    grid style={{dashed, gray!30}},
    mark options={{solid}},
]");

                for (int pi = 0; pi < filteredByProp.Count; pi++)
                {
                    var (prop, points) = filteredByProp[pi];
                    var clr   = lineColors[pi % lineColors.Length];
                    var mark  = pi % 2 == 0 ? "*" : "square*";
                    var label = prop.Title ?? $"P{properties.IndexOf(prop) + 1}";
                    label = label.Length > 30 ? label.Substring(0, 30) + "..." : label;

                    sb.Append($@"\addplot[color={clr}, mark={mark}, mark size=1.5pt, thick, line width=1.2pt] coordinates {{");
                    foreach (var pt in points)
                    {
                        var x = (pt.Date - minDate).TotalDays;
                        sb.Append($"({x.ToString("F0", CultureInfo.InvariantCulture)},{pt.Price.ToString("F0", CultureInfo.InvariantCulture)}) ");
                    }
                    sb.AppendLine($"}};");
                    sb.AppendLine($@"\addlegendentry{{P{properties.IndexOf(prop) + 1}: {Esc(label)}}}");
                }

                sb.AppendLine(@"\end{axis}
\end{tikzpicture}
\end{center}
");

                var singlePoint = filteredByProp.Where(x => x.Points.Count == 1).ToList();
                if (singlePoint.Any())
                {
                    var names = string.Join(", ", singlePoint.Select(x => $"P{properties.IndexOf(x.Prop) + 1}"));
                    sb.AppendLine($@"\vspace{{0.3em}}
{{\small\color{{muted}} \textbf{{Nota:}} {Esc(names)} solo cuentan con un registro de precio.}}
");
                }

                var propsWithChanges = propsWithHistory
                    .Where(p => snapshots.Any(s => s.PropertyId == p.Id && s.HasChanges && (s.ChangedFields ?? "").Contains("Price")))
                    .ToList();

                if (propsWithChanges.Any())
                {
                    sb.AppendLine(@"\vspace{0.5em}
\subsection{Cambios de Precio Detectados}
\begin{itemize}[leftmargin=1.5em]");
                    foreach (var prop in propsWithChanges)
                    {
                        var changes  = snapshots
                            .Where(s => s.PropertyId == prop.Id && s.HasChanges && (s.ChangedFields ?? "").Contains("Price"))
                            .OrderBy(s => s.ScrapedAt)
                            .ToList();
                        var propIdx    = properties.IndexOf(prop) + 1;
                        var shortTitle = (prop.Title ?? $"P{propIdx}").Length > 35 ? prop.Title!.Substring(0, 35) + "..." : prop.Title;
                        sb.AppendLine($@"  \item \textbf{{P{propIdx} -- {Esc(shortTitle)}:}} {changes.Count} cambio(s) de precio detectado(s).");

                        foreach (var chg in changes.Take(3))
                            sb.AppendLine($@"    \begin{{itemize}}[leftmargin=1em]
      \item {chg.ScrapedAt.ToString("dd/MM/yyyy HH:mm")} \textrightarrow\ \textbf{{{FormatPrice(chg.Price.HasValue ? (double)chg.Price.Value : (double?)null, chg.Currency)}}}
    \end{{itemize}}");

                        if (changes.Count > 3)
                            sb.AppendLine($@"    \begin{{itemize}}[leftmargin=1em]
      \item \textcolor{{muted}}{{\small ...y {changes.Count - 3} más}}
    \end{{itemize}}");
                    }
                    sb.AppendLine(@"\end{itemize}");
                }
            }
        }

        sb.AppendLine(@"\newpage");

        // ── Resumen de precios ────────────────────────────────────
        var priced = properties.Where(p => p.Price.HasValue && p.Price > 0).ToList();
        if (priced.Count >= 2)
        {
            sb.AppendLine(@"\section{Resumen de Precios}");
            var byCurrency = priced.GroupBy(p => (p.Currency ?? "CLP").ToUpper()).ToList();
            foreach (var group in byCurrency)
            {
                var prices    = group.Select(p => (double)p.Price!.Value).ToList();
                var avg       = prices.Average();
                var min       = prices.Min();
                var max       = prices.Max();
                var cheapest  = group.OrderBy(p => p.Price).First();
                var expensive = group.OrderByDescending(p => p.Price).First();

                sb.AppendLine($@"
\subsection{{Propiedades en {group.Key}}}
\begin{{itemize}}[leftmargin=1.5em]
  \item Precio promedio: \textbf{{{FormatPrice(avg, group.Key)}}}
  \item Rango: {FormatPrice(min, group.Key)} -- {FormatPrice(max, group.Key)}
  \item Más económica: \textbf{{{Esc((cheapest.Title ?? "").Length > 40 ? cheapest.Title!.Substring(0, 40) + "..." : cheapest.Title)}}} ({FormatPrice((double)cheapest.Price!, group.Key)})
  \item Más cara: \textbf{{{Esc((expensive.Title ?? "").Length > 40 ? expensive.Title!.Substring(0, 40) + "..." : expensive.Title)}}} ({FormatPrice((double)expensive.Price!, group.Key)})
\end{{itemize}}");
            }
        }

        // ── Condición ─────────────────────────────────────────────
        var withCondition = properties.Where(p => !string.IsNullOrEmpty(p.Condition)).ToList();
        if (withCondition.Count > 0)
        {
            var nCount = withCondition.Count(p => p.Condition == "Nuevo");
            var uCount = withCondition.Count(p => p.Condition == "Usado");
            sb.AppendLine($@"
\vspace{{0.5em}}
\subsection{{Estado de las propiedades}}
\begin{{itemize}}[leftmargin=1.5em]
  \item \textcolor{{nuevo}}{{Nuevas: {nCount}}}
  \item \textcolor{{usado}}{{Usadas: {uCount}}}
  \item Sin información: {properties.Count - withCondition.Count}
\end{{itemize}}");
        }

        sb.AppendLine(@"
\vfill
\textcolor{muted}{\rule{\linewidth}{0.4pt}}\\
{\scriptsize\color{muted}
  Este informe comparativo fue generado automáticamente por \textbf{InmobiScrap}.
  Los datos provienen de scraping de portales inmobiliarios chilenos y pueden contener imprecisiones.
}

\end{document}");

        return sb.ToString();
    }

    // ══════════════════════════════════════════════════════════════════════
    // HELPERS — CARDS
    // ══════════════════════════════════════════════════════════════════════

    private static string BuildGlobalAvgCards(ReportData d)
    {
        var cards = new List<string>();
        if (d.GlobalAvgUF.HasValue)
            cards.Add($@"\kpicard{{Precio Promedio (UF)}}{{{d.GlobalAvgUF.Value:F0} UF}}{{promedio general}}");
        if (d.GlobalAvgCLP.HasValue)
        {
            var clpM = d.GlobalAvgCLP.Value / 1_000_000;
            cards.Add($@"\kpicard{{Precio Promedio (CLP)}}{{\${clpM:F1}M}}{{promedio general}}");
        }
        if (cards.Count == 0) return string.Empty;
        if (cards.Count == 1) return cards[0];
        return string.Join(@"\hfill" + Environment.NewLine, cards);
    }

    private static string BuildPricePerSqmCards(ReportData d)
    {
        var cards = new List<string>();
        if (d.GlobalAvgPpsqmUF.HasValue)
            cards.Add($@"\kpicard{{Precio/m\textsuperscript{{2}} (UF)}}{{{d.GlobalAvgPpsqmUF.Value:F1} UF/m\textsuperscript{{2}}}}{{promedio general}}");
        if (d.GlobalAvgPpsqmCLP.HasValue)
        {
            var k = d.GlobalAvgPpsqmCLP.Value / 1_000;
            cards.Add($@"\kpicard{{Precio/m\textsuperscript{{2}} (CLP)}}{{\${k:F0}K/m\textsuperscript{{2}}}}{{promedio general}}");
        }
        if (cards.Count == 0) return string.Empty;
        if (cards.Count == 1) return cards[0];
        return string.Join(@"\hfill" + Environment.NewLine, cards);
    }

    // ══════════════════════════════════════════════════════════════════════
    // HELPERS — BUCKETING DE PRECIOS
    // ══════════════════════════════════════════════════════════════════════

    private static List<PriceRangeRow> BucketPricesUF(List<double> prices)
    {
        if (prices.Count == 0) return new();
        var ranges = new (double Lo, double Hi, string Label)[]
        {
            (0,      500,   "0-500"),
            (500,    1000,  "500-1K"),
            (1000,   2000,  "1K-2K"),
            (2000,   3000,  "2K-3K"),
            (3000,   5000,  "3K-5K"),
            (5000,   10000, "5K-10K"),
            (10000,  double.MaxValue, "10K+"),
        };
        return ranges
            .Select(r => new PriceRangeRow { Range = r.Label, Count = prices.Count(p => p >= r.Lo && p < r.Hi) })
            .Where(r => r.Count > 0)
            .ToList();
    }

    private static List<PriceRangeRow> BucketPricesCLP(List<double> prices)
    {
        if (prices.Count == 0) return new();
        var ranges = new (double Lo, double Hi, string Label)[]
        {
            (0,            30_000_000,  "0-30M"),
            (30_000_000,   60_000_000,  "30M-60M"),
            (60_000_000,   100_000_000, "60M-100M"),
            (100_000_000,  150_000_000, "100M-150M"),
            (150_000_000,  250_000_000, "150M-250M"),
            (250_000_000,  500_000_000, "250M-500M"),
            (500_000_000,  double.MaxValue, "500M+"),
        };
        return ranges
            .Select(r => new PriceRangeRow { Range = r.Label, Count = prices.Count(p => p >= r.Lo && p < r.Hi) })
            .Where(r => r.Count > 0)
            .ToList();
    }

    private static string MonthLabel(int year, int month)
    {
        var months = new[] { "Ene", "Feb", "Mar", "Abr", "May", "Jun", "Jul", "Ago", "Sep", "Oct", "Nov", "Dic" };
        return $"{months[month - 1]}/{year % 100:D2}";
    }

    // ── Escape caracteres especiales de LaTeX ─────────────────────────────────
    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "---";
        return s
            .Replace(@"\", @"\textbackslash{}")
            .Replace("&",  @"\&")
            .Replace("%",  @"\%")
            .Replace("$",  @"\$")
            .Replace("#",  @"\#")
            .Replace("_",  @"\_")
            .Replace("{",  @"\{")
            .Replace("}",  @"\}")
            .Replace("~",  @"\textasciitilde{}")
            .Replace("^",  @"\textasciicircum{}")
            .Replace("…",  @"\ldots{}");
    }

    private static string FormatPrice(double? value, string? currency)
    {
        if (value == null) return "---";
        if ((currency ?? "").ToUpper() == "UF")
            return $"{value.Value:N0} UF";
        if (value >= 1_000_000_000)
            return $@"\${value.Value / 1_000_000_000:F1}B";
        if (value >= 1_000_000)
            return $@"\${value.Value / 1_000_000:F0}M";
        if (value >= 1_000)
            return $@"\${value.Value / 1_000:F0}K";
        return $@"\${value.Value:N0}";
    }

    // ══════════════════════════════════════════════════════════════════════
    // LATEX → PDF COMPILATION
    // ══════════════════════════════════════════════════════════════════════

    private async Task<byte[]> CompileLatexAsync(string latexSource)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"inmobiscrap-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        var texFile = Path.Combine(workDir, "report.tex");
        var pdfFile = Path.Combine(workDir, "report.pdf");

        try
        {
            await File.WriteAllTextAsync(texFile, latexSource, Encoding.UTF8);

            for (int pass = 1; pass <= 2; pass++)
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = "pdflatex",
                    Arguments              = $"-interaction=nonstopmode -output-directory={workDir} {texFile}",
                    WorkingDirectory       = workDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };

                using var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("No se pudo iniciar pdflatex.");

                var stdout = await proc.StandardOutput.ReadToEndAsync();
                var stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (proc.ExitCode != 0)
                {
                    _logger.LogError("pdflatex pass {Pass} failed.\nSTDOUT:\n{Out}\nSTDERR:\n{Err}",
                        pass, stdout, stderr);
                    throw new InvalidOperationException(
                        $"pdflatex falló (código {proc.ExitCode}). Revisa los logs del servidor.");
                }

                _logger.LogDebug("pdflatex pass {Pass} OK", pass);
            }

            if (!File.Exists(pdfFile))
                throw new FileNotFoundException("pdflatex no generó el archivo PDF.");

            return await File.ReadAllBytesAsync(pdfFile);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// DTOs internos del reporte
// ══════════════════════════════════════════════════════════════════════════════

internal class ReportData
{
    // ── Existentes ────────────────────────────────────────────────────────────
    public int                    Total            { get; set; }
    public List<TypeCurrencyRow>  ByType           { get; set; } = new();
    public List<NeighborhoodRow>  TopNeighborhoods { get; set; } = new();
    public List<PriceChangeRow>   RecentChanges    { get; set; } = new();
    public double?                GlobalAvgUF      { get; set; }
    public double?                GlobalAvgCLP     { get; set; }
    public int                    NewLast7Days     { get; set; }
    public int                    WithPriceChanges { get; set; }
    public DateTime               GeneratedAt      { get; set; }

    // ── Nuevos ────────────────────────────────────────────────────────────────
    public List<ConditionDistribRow>    ConditionDistrib       { get; set; } = new();
    public List<PriceByConditionRow>    PriceByCondition       { get; set; } = new();
    public List<MonthlyPublicationRow>  PublicationsByMonth    { get; set; } = new();
    public List<BotSourceRow>           BySource               { get; set; } = new();
    public List<PricePerSqmRow>         PricePerSqmByType      { get; set; } = new();
    public List<PricePerSqmRow>         PricePerSqmByCity      { get; set; } = new();
    public List<PricePerSqmRow>         PricePerSqmByCondition { get; set; } = new();
    public double?                      GlobalAvgPpsqmUF       { get; set; }
    public double?                      GlobalAvgPpsqmCLP      { get; set; }
    public List<ConditionByTypeRow>     ConditionByType        { get; set; } = new();
    public List<PriceRangeRow>          PriceRangesUF          { get; set; } = new();
    public List<PriceRangeRow>          PriceRangesCLP         { get; set; } = new();
    public List<MonthlyPriceRow>        PriceHistory           { get; set; } = new();
}

internal class TypeCurrencyRow
{
    public string  Type         { get; set; } = "";
    public string  Currency     { get; set; } = "CLP";
    public int     Count        { get; set; }
    public double? AvgPrice     { get; set; }
    public double? MinPrice     { get; set; }
    public double? MaxPrice     { get; set; }
    public double? AvgBedrooms  { get; set; }
    public double? AvgBathrooms { get; set; }
    public double? AvgArea      { get; set; }
}

internal class NeighborhoodRow
{
    public string  Name        { get; set; } = "";
    public int     Count       { get; set; }
    public double? AvgPriceUF  { get; set; }
    public double? AvgPriceCLP { get; set; }
}

internal class PriceChangeRow
{
    public string   Title          { get; set; } = "";
    public string   City           { get; set; } = "";
    public double   OldPrice       { get; set; }
    public double   NewPrice       { get; set; }
    public string   Currency       { get; set; } = "CLP";
    public DateTime PriceChangedAt { get; set; }
}

// ── Nuevos DTOs ───────────────────────────────────────────────────────────────

internal class ConditionDistribRow
{
    public string Condition { get; set; } = "";
    public int    Count     { get; set; }
}

internal class PriceByConditionRow
{
    public string Condition { get; set; } = "";
    public string Currency  { get; set; } = "CLP";
    public double Avg       { get; set; }
    public double Min       { get; set; }
    public double Max       { get; set; }
}

internal class MonthlyPublicationRow
{
    public int Year  { get; set; }
    public int Month { get; set; }
    public int Count { get; set; }
}

internal class BotSourceRow
{
    public string Source       { get; set; } = "";
    public long   TotalScraped { get; set; }
}

internal class PricePerSqmRow
{
    public string Name      { get; set; } = "";
    public string Currency  { get; set; } = "CLP";
    public double AvgPerSqm { get; set; }
    public int    Count     { get; set; }
}

internal class ConditionByTypeRow
{
    public string Type  { get; set; } = "";
    public int    Nuevo { get; set; }
    public int    Usado { get; set; }
}

internal class PriceRangeRow
{
    public string Range { get; set; } = "";
    public int    Count { get; set; }
}

internal class MonthlyPriceRow
{
    public DateTime Month    { get; set; }
    public string   Currency { get; set; } = "CLP";
    public double   AvgPrice { get; set; }
}
