﻿namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// This represents information about closed-loop tuning
    /// </summary>
    public sealed class BoardClosedLoop : ModelObject
    {
        /// <summary>
        /// Number of collected data points in the last run or 0 if it failed
        /// </summary>
        public int Points
        {
            get => _points;
            set => SetPropertyValue(ref _points, value);
        }
        private int _points;

        /// <summary>
        /// Number of completed sampling runs
        /// </summary>
        public int Runs
        {
            get => _runs;
            set => SetPropertyValue(ref _runs, value);
        }
        private int _runs;
    }
}