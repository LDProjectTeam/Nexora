using System;
using System.Collections.Generic;
using System.Linq;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;

namespace Vellum.Core.Services
{
    public class VersionSelector : IVersionSelector
    {
        private readonly IDiskSpaceService _diskSpaceService;
        private readonly ISettingsService _settingsService;

        public VersionSelector(IDiskSpaceService diskSpaceService, ISettingsService settingsService)
        {
            _diskSpaceService = diskSpaceService;
            _settingsService = settingsService;
        }

        public VersionSelectionResult SelectBestVersion(
            IEnumerable<GameReleaseCandidate> candidates,
            string targetInstallPath)
        {
            var candidateList = candidates?.ToList() ?? new List<GameReleaseCandidate>();
            var result = new VersionSelectionResult
            {
                AllCandidates = candidateList
            };

            if (candidateList.Count == 0)
            {
                result.StrategyExplanation = Localizer.T("VersionSelectorNoneFound", "Релизы не найдены ни в одном из подключенных источников.");
                return result;
            }

            // Apply custom source priorities from AppSettings if configured
            var sourcePriorities = _settingsService.Settings.SourcePriorities;
            foreach (var c in candidateList)
            {
                if (sourcePriorities.TryGetValue(c.SourceId, out int prio))
                {
                    c.SourcePriority = prio;
                }
            }

            var preferPortable = _settingsService.Settings.PreferPortable;

            var sortedCandidates = candidateList
                .OrderBy(c => c.SourcePriority)
                .ThenByDescending(c => c.Seeders > 0) // Prefer releases that have alive seeders
                .ToList();

            // Find best portable version if available
            var bestPortable = sortedCandidates
                .Where(c => c.VersionType == GameVersionType.Portable)
                .OrderByDescending(c => c.Seeders)
                .ThenByDescending(c => c.PublishDate ?? DateTime.MinValue)
                .FirstOrDefault();

            // Find best alternative version (repack, installer, archive)
            var bestAlternative = sortedCandidates
                .Where(c => c.VersionType == GameVersionType.Installer ||
                            c.VersionType == GameVersionType.Repack ||
                            c.VersionType == GameVersionType.Archive)
                .OrderByDescending(c => c.Seeders)
                .ThenByDescending(c => c.PublishDate ?? DateTime.MinValue)
                .FirstOrDefault();

            GameReleaseCandidate? chosen = null;
            string explanation = string.Empty;

            if (preferPortable && bestPortable != null)
            {
                // If portable has healthy swarm (>= 5 seeders) OR alternative isn't dramatically better
                // (e.g. don't pick a 2-seeder dead portable when a 300-seeder healthy repack is available)
                bool portableIsHealthy = bestPortable.Seeders >= 10 ||
                    (bestAlternative == null || bestPortable.Seeders * 4 >= bestAlternative.Seeders || bestAlternative.Seeders < 5);

                if (portableIsHealthy)
                {
                    chosen = bestPortable;
                    explanation = string.Format(Localizer.T("VersionSelectorChosenPortableFormat", "Выбрана Portable-версия из '{0}' (сиды: {1})."), chosen.SourceName, chosen.Seeders);
                }
                else if (bestAlternative != null)
                {
                    chosen = bestAlternative;
                    explanation = string.Format(Localizer.T("VersionSelectorChosenLowSeedsFormat", "Выбрана версия {0} из '{1}' (сиды: {2}), так как у Portable недостаточно сидов ({3})."), chosen.VersionType, chosen.SourceName, chosen.Seeders, bestPortable.Seeders);
                }
            }

            if (chosen == null)
            {
                if (bestAlternative != null && (!preferPortable || bestPortable == null))
                {
                    chosen = bestAlternative;
                    explanation = string.Format(Localizer.T("VersionSelectorChosenTypeFormat", "Выбрана версия {0} из '{1}' (сиды: {2})."), chosen.VersionType, chosen.SourceName, chosen.Seeders);
                }
                else
                {
                    chosen = bestPortable ?? bestAlternative ?? sortedCandidates
                        .OrderByDescending(c => c.Seeders)
                        .FirstOrDefault();

                    if (chosen != null)
                        explanation = string.Format(Localizer.T("VersionSelectorChosenFormat", "Выбрана доступная раздача из '{0}' (сиды: {1})."), chosen.SourceName, chosen.Seeders);
                }
            }

            if (chosen != null)
            {
                result.SelectedCandidate = chosen;
                result.SpaceRequirement = _diskSpaceService.CalculateRequirement(chosen, targetInstallPath);
                result.StrategyExplanation = explanation;
                return result;
            }

            // Group 3: Any other candidate as last resort
            var fallback = candidateList
                .OrderBy(c => c.SourcePriority)
                .ThenByDescending(c => c.Seeders)
                .FirstOrDefault();

            if (fallback != null)
            {
                result.SelectedCandidate = fallback;
                result.SpaceRequirement = _diskSpaceService.CalculateRequirement(fallback, targetInstallPath);
                result.StrategyExplanation = string.Format(Localizer.T("VersionSelectorFallbackFormat", "Выбрана раздача из источника '{0}'."), fallback.SourceName);
            }
            else
            {
                result.StrategyExplanation = Localizer.T("VersionSelectorUnavailable", "Подходящие версии игры недоступны.");
            }

            return result;
        }
    }
}
