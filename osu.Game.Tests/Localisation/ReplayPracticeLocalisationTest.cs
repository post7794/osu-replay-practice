// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Reflection;
using NUnit.Framework;
using osu.Game.Localisation;

namespace osu.Game.Tests.Localisation
{
    [TestFixture]
    public class ReplayPracticeLocalisationTest
    {
        [TestCase("zh")]
        [TestCase("zh-CN")]
        [TestCase("zh-Hans")]
        public void TestChineseResourcesAreBundledAndFallbackToChinese(string culture)
        {
            using var store = new ResourceManagerLocalisationStore(culture);
            foreach (var property in typeof(ReplayPracticeStrings).GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                string? translated = store.Get(ReplayPracticeStrings.GetKey(property.Name));
                Assert.That(translated, Is.Not.Null.And.Not.Empty, property.Name);
                Assert.That(translated, Is.Not.EqualTo(property.GetValue(null)!.ToString()), property.Name);
            }
            Assert.That(store.Get(ReplayPracticeStrings.GetKey("AutomaticMod")), Does.Contain("{0}").And.Contain("自动操作"));
            Assert.That(store.Get(ReplayPracticeStrings.GetKey("History")), Does.Contain("{0}").And.Contain("{1}"));
        }

        [Test]
        public void TestEnglishFallbacksRemainAvailable()
        {
            using var store = new ResourceManagerLocalisationStore("en");
            Assert.That(store.Get(ReplayPracticeStrings.GetKey("TakeOver")), Is.Null);
            Assert.That(ReplayPracticeStrings.TakeOver.ToString(), Is.EqualTo("Take over now"));
            Assert.That(ReplayPracticeStrings.AutomaticMod("Autoplay").ToString(), Does.Contain("Autoplay"));
        }
    }
}
