using System;
using Gress;

namespace YoutubeDownloader.Utils
{
    public class PercentageProgress : IProgress<Percentage>
    {
        private readonly Action<double> _reporter;

        public PercentageProgress(Action<double> reporter)
        {
            _reporter = reporter;
        }

        public void Report(Percentage value)
        {
            _reporter(value.Fraction);
        }
    }
}
