using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Tryscape.Content.MovingDay;
using Tryscape.Core.Commands;
using Tryscape.Core.Model;
using Tryscape.Infrastructure.Json;

namespace Tryscape.Tests.EditMode.Core
{
    /// <summary>Shared fixture access: tests read the same contracts/v1 files as Python (S18.2).</summary>
    public static class Fixtures
    {
        public static readonly Guid SceneId = Guid.Parse("10000000-0000-4000-8000-000000000001");
        public static readonly Guid ProjectId = Guid.Parse("20000000-0000-4000-8000-000000000001");
        public static readonly Guid SofaId = Guid.Parse("30000000-0000-4000-8000-000000000001");
        public static readonly Guid ConsoleId = Guid.Parse("30000000-0000-4000-8000-000000000002");

        public static string Root
        {
            get
            {
                // app-unity/Assets -> repository root
                var root = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", "..", "contracts", "v1"));
                Assert.IsTrue(Directory.Exists(root), $"contracts/v1 not found at {root}");
                return root;
            }
        }

        public static string FixturePath(string relative) => Path.Combine(Root, "fixtures", relative);
        public static string Text(string relative) => File.ReadAllText(FixturePath(relative));

        public static IEnumerable<string> Files(string relativeDir, string pattern = "*.json")
        {
            foreach (var path in Directory.GetFiles(FixturePath(relativeDir), pattern))
                if (!Path.GetFileName(path).StartsWith("_", StringComparison.Ordinal))
                    yield return path;
        }

        public static SceneSpec Scene(string relative)
        {
            var result = SceneSpecJsonReader.Read(Text(relative));
            Assert.IsTrue(result.Succeeded, $"{relative} failed to parse: {string.Join(", ", result.Errors)}");
            return result.Scene;
        }

        public static SceneSpec F01 => Scene("scenes/valid/f01_direct_blocked.json");
        public static Catalog Catalog => MovingDayPack.CreateCatalog();

        public static SceneSession Session(SceneSpec scene = null)
        {
            Assert.IsTrue(SceneSession.TryCreate(scene ?? F01, Catalog, out var session, out var validation),
                $"fixture scene invalid: {string.Join(", ", validation.Errors)}");
            return session;
        }
    }

    /// <summary>Deterministic IDs for reproducible tests.</summary>
    public sealed class SequenceIds : IIdGenerator
    {
        readonly Queue<Guid> _queue;
        int _counter;

        public SequenceIds(params Guid[] ids)
        {
            _queue = new Queue<Guid>(ids);
        }

        public Guid NewId()
        {
            if (_queue.Count > 0)
                return _queue.Dequeue();
            _counter++;
            return new Guid(_counter, 0, 0x4000, 0x80, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    public static class Cmd
    {
        public static CommandEnvelope Of(SceneCommand command, long expectedRevision, Guid? id = null, Guid? project = null) =>
            new CommandEnvelope(id ?? Guid.NewGuid(), project ?? Fixtures.ProjectId, expectedRevision, command);

        public static CommandEnvelope Move(int x, int z, long rev, Guid? id = null) =>
            Of(new MoveEntityCommand(Fixtures.ConsoleId, x, z), rev, id);
    }
}
