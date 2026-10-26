using MLFLegends.Combat;
using NUnit.Framework;
using UnityEngine;

namespace MLFLegends.Tests.EditMode
{
    // The one move quantization policy (#22): radial dead zone, then 8 equal 45° sectors, to the digital {-1, 0, +1} axes.
    // Pure function: no device, World or asset.
    public class PlayerInputQuantizeTests
    {
        [TestCase(0f, 0f, 0, 0)]
        [TestCase(1f, 0f, 1, 0)]
        [TestCase(-1f, 0f, -1, 0)]
        [TestCase(0f, 1f, 0, 1)]
        [TestCase(0f, -1f, 0, -1)]
        [TestCase(0.70710677f, 0.70710677f, 1, 1)] // keyboard composite diagonal (DigitalNormalized)
        [TestCase(-0.70710677f, -0.70710677f, -1, -1)]
        [TestCase(0.2f, 0.1f, 0, 0)] // inside the 0.25 dead zone
        [TestCase(0.24f, 0f, 0, 0)]
        [TestCase(0.25f, 0f, 1, 0)] // dead zone boundary is inclusive
        [TestCase(0.92f, 0.38f, 1, 0)] // 22.4°: still the X sector
        [TestCase(0.92f, 0.39f, 1, 1)] // 22.97°: diagonal sector
        [TestCase(0.39f, -0.92f, 1, -1)]
        [TestCase(0.38f, -0.92f, 0, -1)]
        [TestCase(0.3f, 0.3f, 1, 1)] // a small but outside-dead-zone diagonal
        public void QuantizesToEightSectorsOutsideTheDeadZone(float x, float y, int expectedX, int expectedZ)
        {
            PlayerInputCapture.Quantize(new Vector2(x, y), out var moveX, out var moveZ);
            Assert.That((moveX, moveZ), Is.EqualTo(((sbyte)expectedX, (sbyte)expectedZ)));
        }

        [Test]
        public void NonFiniteInputIsIdle()
        {
            foreach (var value in new[] { new Vector2(float.NaN, 1f), new Vector2(float.PositiveInfinity, 0f), new Vector2(float.NaN, float.NaN) })
            {
                PlayerInputCapture.Quantize(value, out var moveX, out var moveZ);
                Assert.That((moveX, moveZ), Is.EqualTo(((sbyte)0, (sbyte)0)), value.ToString());
            }
        }

        [Test]
        public void EveryDirectionOfAFullStickIsOneOfEightEqualSectors()
        {
            var counts = new int[9];
            for (var degrees = 0; degrees < 360; degrees++)
            {
                var radians = (degrees + 0.25f) * Mathf.Deg2Rad; // never on a 22.5° + k·45° boundary
                PlayerInputCapture.Quantize(new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)), out var x, out var z);
                Assert.That(x != 0 || z != 0, Is.True, $"{degrees}°");
                counts[(x + 1) * 3 + (z + 1)]++;
            }

            Assert.That(counts[4], Is.Zero);
            for (var i = 0; i < 9; i++)
            {
                if (i != 4)
                    Assert.That(counts[i], Is.EqualTo(45), $"sector {i}");
            }
        }
    }
}
