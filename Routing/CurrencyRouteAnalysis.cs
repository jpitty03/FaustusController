using Newtonsoft.Json;

namespace FaustusController;

public readonly record struct CurrencyRouteAnalysisResult(
    bool RouteFound,
    long CandidateRouteCount,
    long ExpandedStateCount,
    int FreshEdgeCount,
    int ExpiredEdgeCount,
    bool SearchTruncated,
    long? BestTargetUnits,
    bool UsesInventoryBalances,
    bool UsesLiquidityLimits,
    bool UsesGoldCosts);

public sealed class CurrencyRouteAnalyzer
{
    private const int CurrentRequestSchemaVersion = 2;
    private const int CurrentAnalysisSchemaVersion = 2;
    private const int SupportedGraphSchemaVersion = 1;

    public CurrencyRouteRequestFile LoadOrCreateRequest(
        CurrencyCatalogue catalogue,
        string league,
        string requestPath)
    {
        if (!File.Exists(requestPath))
        {
            if (!catalogue.TryGetUniqueByName("Chaos Orb", out var chaos) ||
                !catalogue.TryGetUniqueByName("Divine Orb", out var divine))
            {
                throw new InvalidDataException(
                    "Default route request requires unique Chaos Orb and Divine Orb identities.");
            }

            var request = new CurrencyRouteRequestFile
            {
                SchemaVersion = CurrentRequestSchemaVersion,
                League = league,
                StartCurrency = CreateCurrency(chaos!),
                StartAmount = 5000,
                TargetCurrency = CreateCurrency(divine!),
                MaximumHops = 3,
                MaximumResults = 10,
                MaximumExpandedStates = 100000
            };
            WriteAtomically(requestPath, request);
        }

        var loaded = JsonConvert.DeserializeObject<CurrencyRouteRequestFile>(
            File.ReadAllText(requestPath)) ??
            throw new InvalidDataException("The route request file is empty.");

        if (loaded.SchemaVersion == 1)
        {
            loaded.SchemaVersion = CurrentRequestSchemaVersion;
            WriteAtomically(requestPath, loaded);
        }

        ValidateRequest(loaded, catalogue, league);
        return loaded;
    }

    public CurrencyRouteAnalysisResult Analyze(
        CurrencyCatalogue catalogue,
        string league,
        string graphPath,
        string requestPath,
        string outputPath,
        DateTimeOffset analyzedAtUtc)
    {
        var request = LoadOrCreateRequest(catalogue, league, requestPath);
        if (!File.Exists(graphPath))
        {
            throw new InvalidDataException("The conversion graph file does not exist.");
        }

        var graph = JsonConvert.DeserializeObject<CurrencyConversionGraphFile>(
            File.ReadAllText(graphPath)) ??
            throw new InvalidDataException("The conversion graph file is empty.");
        ValidateGraph(graph, league);

        var vertices = graph.Vertices.ToDictionary(
            vertex => vertex.Metadata,
            StringComparer.Ordinal);
        if (!vertices.TryGetValue(request.StartCurrency.Metadata, out var startCurrency) ||
            !vertices.TryGetValue(request.TargetCurrency.Metadata, out var targetCurrency))
        {
            throw new InvalidDataException(
                "The route request start or target metadata is absent from the graph.");
        }

        var maximumAge = TimeSpan.FromMinutes(graph.MaximumQuoteAgeMinutes);
        var freshEdges = new List<CurrencyConversionGraphEdgeCapture>();
        var expiredEdgeCount = 0;
        foreach (var edge in graph.Edges)
        {
            ValidateEdge(edge, vertices);
            if (edge.CapturedAtUtc > analyzedAtUtc ||
                analyzedAtUtc - edge.CapturedAtUtc > maximumAge)
            {
                expiredEdgeCount++;
                continue;
            }

            freshEdges.Add(edge);
        }

        var adjacency = freshEdges
            .GroupBy(edge => new { edge.OfferedMetadata, edge.WantedMetadata, edge.BookSide })
            .Select(group => group
                .OrderByDescending(edge => edge.CapturedAtUtc)
                .ThenByDescending(edge => edge.CaptureId)
                .First())
            .GroupBy(edge => edge.OfferedMetadata, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(edge => edge.WantedMetadata, StringComparer.Ordinal)
                    .ThenBy(edge => edge.BookSide, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        var search = new RouteSearchState(request.MaximumExpandedStates);
        var rankedRoutes = new List<RouteCandidate>();
        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            request.StartCurrency.Metadata
        };
        var constraints = new RouteConstraints(
            request.InventoryBalances
                .Where(balance => balance.Units > 0)
                .ToDictionary(
                    balance => balance.Metadata,
                    balance => balance.Units,
                    StringComparer.Ordinal),
            request.UseLiquidityLimits,
            request.GoldCostPerHop,
            request.GoldBudget);
        Search(
            request.StartCurrency.Metadata,
            request.StartAmount,
            request.TargetCurrency.Metadata,
            request.MaximumHops,
            adjacency,
            visited,
            [],
            [],
            0,
            constraints,
            search,
            rankedRoutes,
            request.MaximumResults);

        var orderedRoutes = rankedRoutes
            .OrderByDescending(route => route.TargetUnits)
            .ThenBy(route => route.TotalGoldCost)
            .ThenBy(route => route.Remainders.Count(remainder => remainder.Units > 0))
            .ThenBy(route => route.Hops.Count)
            .ThenBy(route => route.PathKey, StringComparer.Ordinal)
            .Take(request.MaximumResults)
            .ToArray();
        var analysis = new CurrencyRouteAnalysisFile
        {
            SchemaVersion = CurrentAnalysisSchemaVersion,
            AnalyzedAtUtc = analyzedAtUtc,
            League = league,
            GraphGeneratedAtUtc = graph.GeneratedAtUtc,
            GraphMaximumQuoteAgeMinutes = graph.MaximumQuoteAgeMinutes,
            Request = request,
            FreshEdgeCount = freshEdges.Count,
            ExpiredEdgeCount = expiredEdgeCount,
            CandidateRouteCount = search.CandidateRouteCount,
            ExpandedStateCount = search.ExpandedStateCount,
            RejectedCycleCount = search.RejectedCycleCount,
            RejectedZeroLotCount = search.RejectedZeroLotCount,
            RejectedOverflowCount = search.RejectedOverflowCount,
            RejectedLiquidityLimitCount = search.RejectedLiquidityLimitCount,
            RejectedGoldBudgetCount = search.RejectedGoldBudgetCount,
            SearchTruncated = search.Truncated,
            UsesInventoryBalances = request.InventoryBalances.Count > 0,
            UsesLiquidityLimits = request.UseLiquidityLimits,
            UsesGoldCosts = request.GoldCostPerHop > 0 || request.GoldBudget > 0,
            Ranking = "Maximum target units, then lower total gold cost, " +
                "fewer stranded remainder currencies, fewer hops, deterministic path",
            Routes = orderedRoutes.Select((route, index) => CreateRouteCapture(
                index + 1,
                route,
                vertices,
                targetCurrency)).ToList()
        };
        analysis.BestRoute = analysis.Routes.FirstOrDefault();
        WriteAtomically(outputPath, analysis);
        return new CurrencyRouteAnalysisResult(
            analysis.BestRoute != null,
            analysis.CandidateRouteCount,
            analysis.ExpandedStateCount,
            analysis.FreshEdgeCount,
            analysis.ExpiredEdgeCount,
            analysis.SearchTruncated,
            analysis.BestRoute?.TargetUnits,
            analysis.UsesInventoryBalances,
            analysis.UsesLiquidityLimits,
            analysis.UsesGoldCosts);
    }

    private static void Search(
        string currentMetadata,
        long currentUnits,
        string targetMetadata,
        int maximumHops,
        IReadOnlyDictionary<string, CurrencyConversionGraphEdgeCapture[]> adjacency,
        ISet<string> visited,
        IReadOnlyList<RouteHop> hops,
        IReadOnlyList<RouteRemainder> remainders,
        long totalGold,
        RouteConstraints constraints,
        RouteSearchState search,
        ICollection<RouteCandidate> routes,
        int maximumResults)
    {
        if (search.Truncated || hops.Count >= maximumHops ||
            !adjacency.TryGetValue(currentMetadata, out var edges))
        {
            return;
        }

        var availableBefore = currentUnits;
        if (constraints.InventoryBalances.TryGetValue(
                currentMetadata,
                out var inventoryUnits))
        {
            try
            {
                availableBefore = checked(currentUnits + inventoryUnits);
            }
            catch (OverflowException)
            {
                search.RejectedOverflowCount++;
                return;
            }
        }

        foreach (var edge in edges)
        {
            if (search.ExpandedStateCount >= search.MaximumExpandedStates)
            {
                search.Truncated = true;
                return;
            }

            search.ExpandedStateCount++;
            if (visited.Contains(edge.WantedMetadata))
            {
                search.RejectedCycleCount++;
                continue;
            }

            var unconstrainedLots = availableBefore / edge.GiveUnits;
            var fillableLots = 0;
            var lotsCappedByLiquidity = false;
            var lots = unconstrainedLots;
            if (constraints.UseLiquidityLimits && edge.ListedCount > 0)
            {
                fillableLots = edge.ListedCount;
                if (lots > fillableLots)
                {
                    lots = fillableLots;
                    lotsCappedByLiquidity = true;
                }
            }

            if (lots <= 0)
            {
                if (constraints.UseLiquidityLimits && edge.ListedCount > 0 &&
                    unconstrainedLots > 0)
                {
                    search.RejectedLiquidityLimitCount++;
                }
                else
                {
                    search.RejectedZeroLotCount++;
                }

                continue;
            }

            long spent;
            long received;
            try
            {
                spent = checked(lots * edge.GiveUnits);
                received = checked(lots * edge.GetUnits);
            }
            catch (OverflowException)
            {
                search.RejectedOverflowCount++;
                continue;
            }

            var hopGoldCost = constraints.GoldCostPerHop;
            var nextTotalGold = totalGold;
            if (hopGoldCost > 0)
            {
                try
                {
                    nextTotalGold = checked(totalGold + hopGoldCost);
                }
                catch (OverflowException)
                {
                    search.RejectedOverflowCount++;
                    continue;
                }

                if (constraints.GoldBudget > 0 && nextTotalGold > constraints.GoldBudget)
                {
                    search.RejectedGoldBudgetCount++;
                    continue;
                }
            }

            var remainder = availableBefore - spent;
            var nextHop = new RouteHop(
                edge,
                availableBefore,
                lots,
                spent,
                received,
                remainder,
                hopGoldCost,
                fillableLots,
                lotsCappedByLiquidity);
            var nextHops = hops.Append(nextHop).ToArray();
            var nextRemainders = remainder > 0
                ? remainders.Append(new RouteRemainder(currentMetadata, remainder)).ToArray()
                : remainders.ToArray();
            if (edge.WantedMetadata == targetMetadata)
            {
                search.CandidateRouteCount++;
                routes.Add(new RouteCandidate(
                    received,
                    nextTotalGold,
                    nextHops,
                    nextRemainders,
                    string.Join(">", nextHops.Select(hop =>
                        $"{hop.Edge.OfferedMetadata}:{hop.Edge.WantedMetadata}"))));
                TrimRoutes(routes, maximumResults);
                continue;
            }

            visited.Add(edge.WantedMetadata);
            Search(
                edge.WantedMetadata,
                received,
                targetMetadata,
                maximumHops,
                adjacency,
                visited,
                nextHops,
                nextRemainders,
                nextTotalGold,
                constraints,
                search,
                routes,
                maximumResults);
            visited.Remove(edge.WantedMetadata);
        }
    }

    private static void TrimRoutes(ICollection<RouteCandidate> routes, int maximumResults)
    {
        if (routes.Count <= maximumResults * 4)
        {
            return;
        }

        var retained = routes
            .OrderByDescending(route => route.TargetUnits)
            .ThenBy(route => route.TotalGoldCost)
            .ThenBy(route => route.Remainders.Count(remainder => remainder.Units > 0))
            .ThenBy(route => route.Hops.Count)
            .ThenBy(route => route.PathKey, StringComparer.Ordinal)
            .Take(maximumResults * 2)
            .ToArray();
        routes.Clear();
        foreach (var route in retained)
        {
            routes.Add(route);
        }
    }

    private static CurrencyRouteCapture CreateRouteCapture(
        int rank,
        RouteCandidate route,
        IReadOnlyDictionary<string, CurrencyCapture> vertices,
        CurrencyCapture targetCurrency)
    {
        return new CurrencyRouteCapture
        {
            Rank = rank,
            TargetCurrency = targetCurrency,
            TargetUnits = route.TargetUnits,
            HopCount = route.Hops.Count,
            StrandedRemainderCurrencyCount = route.Remainders.Count(
                remainder => remainder.Units > 0),
            TotalGoldCost = route.TotalGoldCost,
            Hops = route.Hops.Select((hop, index) => new CurrencyRouteHopCapture
            {
                Sequence = index + 1,
                OfferedCurrency = vertices[hop.Edge.OfferedMetadata],
                WantedCurrency = vertices[hop.Edge.WantedMetadata],
                AvailableBefore = hop.AvailableBefore,
                Lots = hop.Lots,
                GiveUnitsPerLot = hop.Edge.GiveUnits,
                GetUnitsPerLot = hop.Edge.GetUnits,
                Spent = hop.Spent,
                Received = hop.Received,
                Remainder = hop.Remainder,
                CaptureId = hop.Edge.CaptureId,
                CapturedAtUtc = hop.Edge.CapturedAtUtc,
                BookSide = hop.Edge.BookSide,
                Coherence = hop.Edge.Coherence,
                GoldCost = hop.GoldCost,
                FillableLots = hop.FillableLots,
                LotsCappedByLiquidity = hop.LotsCappedByLiquidity
            }).ToList(),
            Remainders = route.Remainders.Select(remainder => new CurrencyRouteRemainderCapture
            {
                Currency = vertices[remainder.Metadata],
                Units = remainder.Units
            }).ToList()
        };
    }

    private static void ValidateRequest(
        CurrencyRouteRequestFile request,
        CurrencyCatalogue catalogue,
        string league)
    {
        if (request.SchemaVersion != CurrentRequestSchemaVersion ||
            !string.Equals(request.League, league, StringComparison.Ordinal) ||
            request.StartAmount <= 0 || request.StartAmount > 1000000000000 ||
            request.MaximumHops is < 1 or > 4 ||
            request.MaximumResults is < 1 or > 50 ||
            request.MaximumExpandedStates is < 1000 or > 1000000 ||
            string.IsNullOrWhiteSpace(request.StartCurrency.Metadata) ||
            string.IsNullOrWhiteSpace(request.TargetCurrency.Metadata) ||
            request.StartCurrency.Metadata == request.TargetCurrency.Metadata ||
            !catalogue.TryGetByMetadata(request.StartCurrency.Metadata, out _) ||
            !catalogue.TryGetByMetadata(request.TargetCurrency.Metadata, out _) ||
            request.GoldCostPerHop < 0 || request.GoldBudget < 0)
        {
            throw new InvalidDataException(
                "Route request schema, league, identities, amount, or search bounds are invalid.");
        }

        var balanceMetadata = new HashSet<string>(StringComparer.Ordinal);
        foreach (var balance in request.InventoryBalances)
        {
            if (string.IsNullOrWhiteSpace(balance.Metadata) || balance.Units <= 0 ||
                balance.Metadata == request.TargetCurrency.Metadata ||
                !catalogue.TryGetByMetadata(balance.Metadata, out _) ||
                !balanceMetadata.Add(balance.Metadata))
            {
                throw new InvalidDataException(
                    "Route request inventory balances contain an invalid, duplicate, " +
                    "or target currency entry.");
            }
        }
    }

    private static void ValidateGraph(CurrencyConversionGraphFile graph, string league)
    {
        if (graph.SchemaVersion != SupportedGraphSchemaVersion ||
            !string.Equals(graph.League, league, StringComparison.Ordinal) ||
            graph.GeneratedAtUtc == default || graph.MaximumQuoteAgeMinutes <= 0 ||
            graph.VertexCount != graph.Vertices.Count || graph.EdgeCount != graph.Edges.Count)
        {
            throw new InvalidDataException("Conversion graph metadata is invalid for route analysis.");
        }

        if (graph.Vertices.Select(vertex => vertex.Metadata).Distinct(StringComparer.Ordinal).Count() !=
            graph.Vertices.Count)
        {
            throw new InvalidDataException("Conversion graph contains duplicate vertices.");
        }
    }

    private static void ValidateEdge(
        CurrencyConversionGraphEdgeCapture edge,
        IReadOnlyDictionary<string, CurrencyCapture> vertices)
    {
        if (!vertices.ContainsKey(edge.OfferedMetadata) ||
            !vertices.ContainsKey(edge.WantedMetadata) ||
            edge.OfferedMetadata == edge.WantedMetadata || edge.CaptureId == Guid.Empty ||
            edge.CapturedAtUtc == default || edge.GetUnits <= 0 || edge.GiveUnits <= 0 ||
            edge.ListedCount < 0 ||
            !RationalExchangeRate.TryCreate(edge.RawGet, edge.RawGive, out var rate) ||
            rate!.GetUnits != edge.GetUnits || rate.GiveUnits != edge.GiveUnits ||
            edge.BookSide is not "ImmediateBook" and not "CompetingBook")
        {
            throw new InvalidDataException("Conversion graph contains an invalid route edge.");
        }
    }

    private static CurrencyCapture CreateCurrency(CurrencyIdentity currency)
    {
        return new CurrencyCapture
        {
            Metadata = currency.Metadata,
            Hash = currency.Hash,
            Name = currency.Name
        };
    }

    private static void WriteAtomically(string path, object value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(value, Formatting.Indented));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed class RouteSearchState(int maximumExpandedStates)
    {
        public int MaximumExpandedStates { get; } = maximumExpandedStates;
        public long CandidateRouteCount { get; set; }
        public long ExpandedStateCount { get; set; }
        public long RejectedCycleCount { get; set; }
        public long RejectedZeroLotCount { get; set; }
        public long RejectedOverflowCount { get; set; }
        public long RejectedLiquidityLimitCount { get; set; }
        public long RejectedGoldBudgetCount { get; set; }
        public bool Truncated { get; set; }
    }

    private sealed record RouteConstraints(
        IReadOnlyDictionary<string, long> InventoryBalances,
        bool UseLiquidityLimits,
        long GoldCostPerHop,
        long GoldBudget);

    private sealed record RouteHop(
        CurrencyConversionGraphEdgeCapture Edge,
        long AvailableBefore,
        long Lots,
        long Spent,
        long Received,
        long Remainder,
        long GoldCost,
        int FillableLots,
        bool LotsCappedByLiquidity);

    private sealed record RouteRemainder(string Metadata, long Units);

    private sealed record RouteCandidate(
        long TargetUnits,
        long TotalGoldCost,
        IReadOnlyList<RouteHop> Hops,
        IReadOnlyList<RouteRemainder> Remainders,
        string PathKey);
}
