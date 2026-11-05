using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tryscape.Content.MovingDay;
using Tryscape.Core.Commands;
using Tryscape.Core.Model;
using Tryscape.Core.Validation;
using Tryscape.Infrastructure.Json;

namespace Tryscape.Tests.EditMode.Core
{
    /// <summary>C# side of the shared contract fixtures (contracts/v1); Python checks the same files.</summary>
    public sealed class ContractFixtureTests
    {
        [Test]
        public void ValidScenesParseAndPassSemanticValidation()
        {
            var files = Fixtures.Files("scenes/valid").ToArray();
            Assert.That(files.Length, Is.GreaterThanOrEqualTo(11));
            foreach (var path in files)
            {
                var parsed = SceneSpecJsonReader.Read(File.ReadAllText(path));
                Assert.IsTrue(parsed.Succeeded, $"{Path.GetFileName(path)}: {string.Join(", ", parsed.Errors)}");
                var validation = SceneValidator.Validate(parsed.Scene, Fixtures.Catalog);
                Assert.IsTrue(validation.IsValid, $"{Path.GetFileName(path)}: {string.Join(", ", validation.Errors)}");
            }
        }

        [Test]
        public void ShapeInvalidScenesAreRejectedByTheReader()
        {
            var files = Fixtures.Files("scenes/invalid_schema").ToArray();
            Assert.That(files.Length, Is.GreaterThanOrEqualTo(20));
            foreach (var path in files)
                Assert.IsFalse(SceneSpecJsonReader.Read(File.ReadAllText(path)).Succeeded, Path.GetFileName(path));
        }

        [TestCase("duplicate_json_key.json", ShapeCodes.DuplicateKey)]
        [TestCase("unknown_top_level_field.json", ShapeCodes.UnknownField)]
        [TestCase("prefab_path_override.json", ShapeCodes.UnknownField)]
        [TestCase("arbitrary_yaw.json", ShapeCodes.InvalidEnum)]
        [TestCase("fractional_coordinate.json", ShapeCodes.WrongType)]
        [TestCase("coordinate_as_string.json", ShapeCodes.WrongType)]
        [TestCase("null_for_required.json", ShapeCodes.WrongType)]
        [TestCase("missing_required_field.json", ShapeCodes.MissingField)]
        [TestCase("unsupported_major_version.json", ShapeCodes.UnsupportedVersion)]
        [TestCase("uppercase_uuid.json", ShapeCodes.InvalidFormat)]
        [TestCase("label_control_character.json", ShapeCodes.InvalidFormat)]
        [TestCase("label_too_long.json", ShapeCodes.OutOfRange)]
        [TestCase("room_too_small.json", ShapeCodes.OutOfRange)]
        [TestCase("empty_entities.json", ShapeCodes.TooFewItems)]
        [TestCase("model_invented_confirmation_source.json", ShapeCodes.InvalidEnum)]
        public void ShapeErrorsCarryTheExpectedCode(string file, string code)
        {
            var result = SceneSpecJsonReader.Read(Fixtures.Text("scenes/invalid_schema/" + file));
            Assert.IsTrue(result.Errors.Any(e => e.Code == code), $"{file}: got {string.Join(", ", result.Errors)}");
        }

        [Test]
        public void SemanticInvalidScenesReportTheirExpectedCode()
        {
            var expected = JArray.Parse(Fixtures.Text("scenes/invalid_semantic/_expected_codes.json"));
            Assert.That(expected.Count, Is.GreaterThanOrEqualTo(14));
            foreach (var item in expected)
            {
                var file = (string)item["file"];
                var parsed = SceneSpecJsonReader.Read(Fixtures.Text("scenes/invalid_semantic/" + file));
                Assert.IsTrue(parsed.Succeeded, $"{file} must be shape-valid: {string.Join(", ", parsed.Errors)}");
                var validation = SceneValidator.Validate(parsed.Scene, Fixtures.Catalog);
                Assert.IsTrue(validation.Has((string)item["expected_code"]),
                    $"{file}: expected {(string)item["expected_code"]}, got {string.Join(", ", validation.Errors)}");
            }
        }

        [TestCase("{'schema_version': '1.0.0'}")]
        [TestCase("{\"a\": 1 /* comment */}")]
        [TestCase("{a: 1}")]
        [TestCase("{\"a\": NaN}")]
        [TestCase("{\"revision\": 999999999999999999999999}")]
        [TestCase("{\"a\": 1,}")]
        public void NonStandardJsonIsRejected(string json)
        {
            Assert.IsFalse(SceneSpecJsonReader.Read(json).Succeeded, json);
        }

        [Test]
        public void OversizedInputIsRejectedBeforeParsing()
        {
            var huge = "{\"x\": \"" + new string('a', SceneSpecJsonReader.MaxChars) + "\"}";
            var result = SceneSpecJsonReader.Read(huge);
            Assert.AreEqual(ShapeCodes.TooLarge, result.Errors.Single().Code);
        }

        [Test]
        public void CommandErrorWireNamesMatchTheSharedSchema()
        {
            var schema = JObject.Parse(File.ReadAllText(Path.Combine(Fixtures.Root, "schemas", "command.schema.json")));
            var allowed = schema.SelectToken("$defs.command_result.properties.error_code.oneOf[1].enum")
                .Values<string>().OrderBy(s => s).ToArray();
            CollectionAssert.AreEqual(allowed, CommandErrorCodes.AllWireNames.OrderBy(s => s).ToArray());
        }

        [Test]
        public void PackCatalogMatchesTheSharedCatalogFixture()
        {
            var json = JObject.Parse(Fixtures.Text("catalog/moving_day_1.0.0.json"));
            var catalog = MovingDayPack.CreateCatalog();
            Assert.AreEqual((string)json["catalog_version"], catalog.CatalogVersion);
            Assert.AreEqual((string)json["scenario_pack_id"], catalog.ScenarioPackId);
            var entries = (JArray)json["entries"];
            Assert.AreEqual(entries.Count, catalog.Entries.Count());
            foreach (var e in entries)
            {
                Assert.IsTrue(catalog.TryGet((string)e["catalog_id"], out var entry), (string)e["catalog_id"]);
                Assert.AreEqual((int)e["footprint_width_mm"], entry.FootprintWidthMm);
                Assert.AreEqual((int)e["footprint_depth_mm"], entry.FootprintDepthMm);
                Assert.AreEqual((int)e["rendered_height_mm"], entry.RenderedHeightMm);
                Assert.AreEqual((string)e["prefab_key"], entry.PrefabKey);
                Assert.AreEqual((string)e["asset_license_id"], entry.AssetLicenseId);
                Assert.AreEqual((int)e["editable_pose_constraints"]["snap_mm"], entry.SnapMm);
                CollectionAssert.AreEqual(e["editable_pose_constraints"]["allowed_yaw_deg"].Values<int>().ToArray(), entry.AllowedYawDeg);
                CollectionAssert.AreEqual(e["allowed_roles"].Values<string>().ToArray(),
                    entry.AllowedRoles.Select(r => r.ToString().ToLowerInvariant()).ToArray());
                var collision = (string)e["collision_participation"];
                Assert.AreEqual(collision == "floor_obstruction" ? CollisionParticipation.FloorObstruction
                    : collision == "payload" ? CollisionParticipation.Payload : CollisionParticipation.None, entry.Collision);
            }
        }

        [Test]
        public void TemplateReproducesFixtureF01AndF02()
        {
            var ids = new SequenceIds(Fixtures.SceneId, Fixtures.ProjectId, Fixtures.SofaId, Fixtures.ConsoleId);
            var assembled = MovingDayTemplate.Assemble(MovingDayParameters.Example, ids);
            Assert.IsTrue(assembled.ContentEquals(Fixtures.F01), "Example template must equal fixture F01");
            Assert.AreEqual(0, assembled.Revision);

            var parked = MovingDayTemplate.Assemble(MovingDayParameters.Example.With(obstruction: ObstructionState.ParkedSouth),
                new SequenceIds(Fixtures.SceneId, Fixtures.ProjectId, Fixtures.SofaId, Fixtures.ConsoleId));
            var f02 = Fixtures.Scene("scenes/valid/f02_console_parked_south.json");
            Assert.IsTrue(parked.ContentEquals(f02), "Parked-south template must match F02 layout");
        }

        [Test]
        public void TemplateRoomSizeChangesBoundsButNotInternalCoordinates()
        {
            var a = MovingDayTemplate.Assemble(MovingDayParameters.Example, new SequenceIds());
            var b = MovingDayTemplate.Assemble(MovingDayParameters.Example.With(widthMm: 7000, depthMm: 6000), new SequenceIds());
            Assert.AreEqual(7000, b.Room.WidthMm);
            Assert.AreEqual(6000, b.Room.DepthMm);
            Assert.IsFalse(a.Room.Equals(b.Room));
            CollectionAssert.AreEqual(a.Entities.Select(e => e.Pose), b.Entities.Select(e => e.Pose));
            Assert.IsTrue(SceneValidator.Validate(b, Fixtures.Catalog).IsValid);
        }

        [TestCase(4999, 5000)]
        [TestCase(8001, 5000)]
        [TestCase(6000, 4000)]
        public void TemplateRejectsUnsupportedRoomDimensions(int width, int depth)
        {
            Assert.Throws<System.ArgumentException>(() =>
                MovingDayTemplate.Assemble(MovingDayParameters.Example.With(widthMm: width, depthMm: depth), new SequenceIds()));
        }

        [Test]
        public void EverySupportedRoomSizeProducesAValidScene()
        {
            for (var w = RoomSpec.MinDimensionMm; w <= RoomSpec.MaxDimensionMm; w += 500)
                for (var d = RoomSpec.MinDimensionMm; d <= RoomSpec.MaxDimensionMm; d += 500)
                    foreach (var obstruction in new[] { ObstructionState.Center, ObstructionState.ParkedSouth })
                    {
                        var scene = MovingDayTemplate.Assemble(new MovingDayParameters(w, d, obstruction, MovingDayPack.DirectRouteId), new SequenceIds());
                        var validation = SceneValidator.Validate(scene, Fixtures.Catalog);
                        Assert.IsTrue(validation.IsValid, $"{w}x{d} {obstruction}: {string.Join(", ", validation.Errors)}");
                    }
        }
    }
}
