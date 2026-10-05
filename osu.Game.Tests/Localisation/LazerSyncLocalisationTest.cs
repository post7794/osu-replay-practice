// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Reflection;
using NUnit.Framework;
using osu.Game.Localisation;

namespace osu.Game.Tests.Localisation
{
    public class LazerSyncLocalisationTest
    {
        [TestCase("zh")]
        [TestCase("zh-CN")]
        [TestCase("zh-Hans")]
        public void TestChineseResources(string culture)
        {
            using var store = new ResourceManagerLocalisationStore(culture);
            foreach (var property in typeof(LazerSyncStrings).GetProperties(BindingFlags.Public | BindingFlags.Static))
                Assert.That(store.Get($"osu.Game.Localisation.LazerSync:{property.Name}"), Is.Not.Null.And.Not.Empty, property.Name);
            Assert.That(store.Get("osu.Game.Localisation.LazerSync:Result"), Does.Contain("{0}").And.Contain("{1}").And.Contain("{2}").And.Contain("{3}").And.Contain("{4}"));
            Assert.That(store.Get("osu.Game.Localisation.LazerSync:Enable"), Does.Contain("双向同步"));
        }

        [Test]
        public void TestEnglishFallback() => Assert.That(LazerSyncStrings.Header.ToString(), Does.Contain("two-way"));
    }
}
