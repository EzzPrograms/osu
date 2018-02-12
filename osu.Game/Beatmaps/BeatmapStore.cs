// Copyright (c) 2007-2018 ppy Pty Ltd <contact@ppy.sh>.
// Licensed under the MIT Licence - https://raw.githubusercontent.com/ppy/osu/master/LICENCE

using System;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using osu.Game.Database;

namespace osu.Game.Beatmaps
{
    /// <summary>
    /// Handles the storage and retrieval of Beatmaps/BeatmapSets to the database backing
    /// </summary>
    public class BeatmapStore : DatabaseBackedStore
    {
        public event Action<BeatmapSetInfo> BeatmapSetAdded;
        public event Action<BeatmapSetInfo> BeatmapSetRemoved;

        public event Action<BeatmapInfo> BeatmapHidden;
        public event Action<BeatmapInfo> BeatmapRestored;

        public BeatmapStore(DatabaseContextFactory factory)
            : base(factory)
        {
        }

        /// <summary>
        /// Add a <see cref="BeatmapSetInfo"/> to the database.
        /// </summary>
        /// <param name="beatmapSet">The beatmap to add.</param>
        public void Add(BeatmapSetInfo beatmapSet)
        {
            using (var db = ContextFactory.GetForWrite())
            {
                var context = db.Context;

                foreach (var beatmap in beatmapSet.Beatmaps.Where(b => b.Metadata != null))
                {
                    // If we detect a new metadata object it'll be attached to the current context so it can be reused
                    // to prevent duplicate entries when persisting. To accomplish this we look in the cache (.Local)
                    // of the corresponding table (.Set<BeatmapMetadata>()) for matching entries to our criteria.
                    var contextMetadata = context.Set<BeatmapMetadata>().Local.SingleOrDefault(e => e.Equals(beatmap.Metadata));
                    if (contextMetadata != null)
                        beatmap.Metadata = contextMetadata;
                    else
                        context.BeatmapMetadata.Attach(beatmap.Metadata);
                }

                context.BeatmapSetInfo.Attach(beatmapSet);
                BeatmapSetAdded?.Invoke(beatmapSet);
            }
        }

        /// <summary>
        /// Update a <see cref="BeatmapSetInfo"/> in the database. TODO: This only supports very basic updates currently.
        /// </summary>
        /// <param name="beatmapSet">The beatmap to update.</param>
        public void Update(BeatmapSetInfo beatmapSet)
        {
            BeatmapSetRemoved?.Invoke(beatmapSet);

            using (var usage = ContextFactory.GetForWrite())
                usage.Context.BeatmapSetInfo.Update(beatmapSet);

            BeatmapSetAdded?.Invoke(beatmapSet);
        }

        /// <summary>
        /// Delete a <see cref="BeatmapSetInfo"/> from the database.
        /// </summary>
        /// <param name="beatmapSet">The beatmap to delete.</param>
        /// <returns>Whether the beatmap's <see cref="BeatmapSetInfo.DeletePending"/> was changed.</returns>
        public bool Delete(BeatmapSetInfo beatmapSet)
        {
            using ( ContextFactory.GetForWrite())
            {
                Refresh(ref beatmapSet, BeatmapSets);

                if (beatmapSet.DeletePending) return false;
                beatmapSet.DeletePending = true;
            }

            BeatmapSetRemoved?.Invoke(beatmapSet);
            return true;
        }

        /// <summary>
        /// Restore a previously deleted <see cref="BeatmapSetInfo"/>.
        /// </summary>
        /// <param name="beatmapSet">The beatmap to restore.</param>
        /// <returns>Whether the beatmap's <see cref="BeatmapSetInfo.DeletePending"/> was changed.</returns>
        public bool Undelete(BeatmapSetInfo beatmapSet)
        {
            using ( ContextFactory.GetForWrite())
            {
                Refresh(ref beatmapSet, BeatmapSets);

                if (!beatmapSet.DeletePending) return false;
                beatmapSet.DeletePending = false;
            }

            BeatmapSetAdded?.Invoke(beatmapSet);
            return true;
        }

        /// <summary>
        /// Hide a <see cref="BeatmapInfo"/> in the database.
        /// </summary>
        /// <param name="beatmap">The beatmap to hide.</param>
        /// <returns>Whether the beatmap's <see cref="BeatmapInfo.Hidden"/> was changed.</returns>
        public bool Hide(BeatmapInfo beatmap)
        {
            using (ContextFactory.GetForWrite())
            {
                Refresh(ref beatmap, Beatmaps);

                if (beatmap.Hidden) return false;
                beatmap.Hidden = true;

                BeatmapHidden?.Invoke(beatmap);
            }

            return true;
        }

        /// <summary>
        /// Restore a previously hidden <see cref="BeatmapInfo"/>.
        /// </summary>
        /// <param name="beatmap">The beatmap to restore.</param>
        /// <returns>Whether the beatmap's <see cref="BeatmapInfo.Hidden"/> was changed.</returns>
        public bool Restore(BeatmapInfo beatmap)
        {
            using (ContextFactory.GetForWrite())
            {
                Refresh(ref beatmap, Beatmaps);

                if (!beatmap.Hidden) return false;
                beatmap.Hidden = false;
            }

            BeatmapRestored?.Invoke(beatmap);
            return true;
        }

        public override void Cleanup() => Cleanup(_ => true);

        public void Cleanup(Expression<Func<BeatmapSetInfo, bool>> query)
        {
            using (var usage = ContextFactory.GetForWrite())
            {
                var context = usage.Context;

                var purgeable = context.BeatmapSetInfo.Where(s => s.DeletePending && !s.Protected)
                                       .Where(query)
                                       .Include(s => s.Beatmaps).ThenInclude(b => b.Metadata)
                                       .Include(s => s.Beatmaps).ThenInclude(b => b.BaseDifficulty)
                                       .Include(s => s.Metadata);

                // metadata is M-N so we can't rely on cascades
                context.BeatmapMetadata.RemoveRange(purgeable.Select(s => s.Metadata));
                context.BeatmapMetadata.RemoveRange(purgeable.SelectMany(s => s.Beatmaps.Select(b => b.Metadata).Where(m => m != null)));

                // todo: we can probably make cascades work here with a FK in BeatmapDifficulty. just make to make it work correctly.
                context.BeatmapDifficulty.RemoveRange(purgeable.SelectMany(s => s.Beatmaps.Select(b => b.BaseDifficulty)));

                // cascades down to beatmaps.
                context.BeatmapSetInfo.RemoveRange(purgeable);
            }
        }

        public IQueryable<BeatmapSetInfo> BeatmapSets => ContextFactory.Get().BeatmapSetInfo
                                                                     .Include(s => s.Metadata)
                                                                     .Include(s => s.Beatmaps).ThenInclude(s => s.Ruleset)
                                                                     .Include(s => s.Beatmaps).ThenInclude(b => b.BaseDifficulty)
                                                                     .Include(s => s.Beatmaps).ThenInclude(b => b.Metadata)
                                                                     .Include(s => s.Files).ThenInclude(f => f.FileInfo);

        public IQueryable<BeatmapInfo> Beatmaps => ContextFactory.Get().BeatmapInfo
                                                               .Include(b => b.BeatmapSet).ThenInclude(s => s.Metadata)
                                                               .Include(b => b.BeatmapSet).ThenInclude(s => s.Files).ThenInclude(f => f.FileInfo)
                                                               .Include(b => b.Metadata)
                                                               .Include(b => b.Ruleset)
                                                               .Include(b => b.BaseDifficulty);
    }
}
