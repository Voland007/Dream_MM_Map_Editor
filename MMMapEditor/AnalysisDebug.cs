// Copyright (c) Voland007 2026. All rights reserved.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.


﻿using System;
using System.Diagnostics;
using System.Threading;

namespace MMMapEditor
{
    /// <summary>
    /// Единая точка настройки и фильтрации отладочных логов анализа.
    /// </summary>
    public static class AnalysisDebug
    {
        private static readonly AsyncLocal<(byte X, byte Y)?> _currentCell = new AsyncLocal<(byte X, byte Y)?>();

        public static bool Enabled { get; private set; } = true;
        public static bool EnableGlobalLogs { get; private set; } = false;
        public static byte? TargetX { get; private set; } = 3;
        public static byte? TargetY { get; private set; } = 3;

        public static void Configure(bool enabled, byte? targetX = null, byte? targetY = null, bool enableGlobalLogs = false)
        {
            Enabled = enabled;
            TargetX = targetX;
            TargetY = targetY;
            EnableGlobalLogs = enableGlobalLogs;
        }

        public static bool IsEnabledFor(byte x, byte y)
        {
            if (!Enabled)
                return false;

            if (TargetX.HasValue && x != TargetX.Value)
                return false;

            if (TargetY.HasValue && y != TargetY.Value)
                return false;

            return true;
        }

        public static bool IsEnabledFor(OvrObject obj)
        {
            return obj != null && IsEnabledFor(obj.X, obj.Y);
        }

        public static IDisposable BeginCellScope(byte x, byte y)
        {
            var previous = _currentCell.Value;
            _currentCell.Value = (x, y);
            return new Scope(() => _currentCell.Value = previous);
        }

        public static IDisposable BeginCellScope(OvrObject obj)
        {
            return obj == null ? new Scope(() => { }) : BeginCellScope(obj.X, obj.Y);
        }

        public static void WriteLine(string message)
        {
            if (!Enabled)
                return;

            var currentCell = _currentCell.Value;
            if (currentCell.HasValue)
            {
                if (IsEnabledFor(currentCell.Value.X, currentCell.Value.Y))
                    Debug.WriteLine(message);

                return;
            }

            if (EnableGlobalLogs)
                Debug.WriteLine(message);
        }

        private sealed class Scope : IDisposable
        {
            private readonly Action _onDispose;
            private bool _disposed;

            public Scope(Action onDispose)
            {
                _onDispose = onDispose;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                _onDispose?.Invoke();
            }
        }
    }
}