using System.Text;

using GitTfs.Core;

using Xunit;

namespace GitTfs.Test.Core
{
    public class GitHelpersTests : BaseTest
    {
        // git output is decoded as strict UTF-8 (new UTF8Encoding(false, true)),
        // which matches production. 0xFF is never a valid UTF-8 byte, so decoding
        // it throws a DecoderFallbackException.
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private static string DecodeStrict(byte[] bytes)
        {
            using (var reader = new StreamReader(new MemoryStream(bytes), StrictUtf8))
                return reader.ReadToEnd();
        }

        [Fact]
        public void InvalidUtf8OutputThrowsContextualGitTfsException()
        {
            var invalidBytes = new byte[] { 0x41, 0xFF, 0x42 };

            var ex = Assert.Throws<GitTfsException>(
                () => GitHelpers.TranslateDecoderErrors(() => DecodeStrict(invalidBytes)));

            Assert.IsType<DecoderFallbackException>(ex.InnerException);
        }

        [Fact]
        public void ValidUtf8OutputPassesThroughUnchanged()
        {
            var multibyte = "日本語 history";
            var bytes = StrictUtf8.GetBytes(multibyte);

            var result = GitHelpers.TranslateDecoderErrors(() => DecodeStrict(bytes));

            Assert.Equal(multibyte, result);
        }

        [Fact]
        public void NonDecoderExceptionsArePropagatedUnchanged()
        {
            Assert.Throws<InvalidOperationException>(
                () => GitHelpers.TranslateDecoderErrors<int>(() => throw new InvalidOperationException("boom")));
        }

        [Fact]
        public void ActionOverloadTranslatesInvalidUtf8Output()
        {
            var invalidBytes = new byte[] { 0xC0, 0x80 }; // overlong encoding, rejected by strict UTF-8

            var ex = Assert.Throws<GitTfsException>(
                () => GitHelpers.TranslateDecoderErrors(() => { DecodeStrict(invalidBytes); }));

            Assert.IsType<DecoderFallbackException>(ex.InnerException);
        }
    }
}
