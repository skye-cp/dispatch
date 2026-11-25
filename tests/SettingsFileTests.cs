using System;
using System.IO;
using Dispatch.Model;
using Xunit;

namespace Dispatch.Tests
{
    /// <summary>The settings file on disk: written safely, and never overwritten when it couldn't be read.</summary>
    public sealed class SettingsFileTests : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "dispatch-tests-" + Guid.NewGuid().ToString("N"));

        private string FilePath => Path.Combine(folder, "settings.ini");

        public void Dispose()
        {
            if (File.Exists(FilePath))
            {
                File.SetAttributes(FilePath, FileAttributes.Normal);
            }

            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void SavingCreatesTheFolderAndLeavesNoTemporaryFile()
        {
            var s = Settings.Load(FilePath);
            Assert.False(s.ReadOnly);
            s.Set("sort", "Age");
            Assert.True(s.Save());
            s.Set("sort", "Map");
            Assert.True(s.Save());

            Assert.Equal("Map", Settings.Load(FilePath).Get("sort"));
            Assert.False(File.Exists(FilePath + ".tmp"));
        }

        [Fact]
        public void AFileThatCannotBeReadIsNeverOverwritten()
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(FilePath, "followed=precious\n");
            Settings s;
            using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                s = Settings.Load(FilePath);
            }

            Assert.True(s.ReadOnly);
            s.Set("followed", "");
            s.Save();
            Assert.Equal("followed=precious\n", File.ReadAllText(FilePath));
        }

        [Fact]
        public void AReadOnlyFileIsStillUpdated()
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(FilePath, "sort=Ping\n");
            File.SetAttributes(FilePath, FileAttributes.ReadOnly);
            var s = Settings.Load(FilePath);
            s.Set("sort", "Age");
            Assert.True(s.Save());
            Assert.Equal("Age", Settings.Load(FilePath).Get("sort"));
        }

        [Fact]
        public void ASaveThatCannotWriteSaysSo()
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(FilePath, "sort=Ping\n");
            var s = Settings.Load(FilePath);
            s.Set("sort", "Age");
            using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.False(s.Save());
            }
        }
    }
}
