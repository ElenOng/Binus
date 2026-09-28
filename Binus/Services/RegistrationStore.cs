using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Binus.Services
{
    public class RegistrationRecord
    {
        public List<int> AnakKes { get; set; } = new();
        public int? ShiftId { get; set; }
        public DateTime SavedAt { get; set; }
    }

    // Simple in-memory store keyed by binusian id. Not for production.
    public class RegistrationStore
    {
        private readonly ConcurrentDictionary<string, RegistrationRecord> _store = new();

        public RegistrationRecord? Get(string binusianId)
        {
            if (string.IsNullOrEmpty(binusianId)) return null;
            _store.TryGetValue(binusianId, out var rec);
            return rec;
        }

        public void Save(string binusianId, List<int> anakKes, int? shiftId)
        {
            if (string.IsNullOrEmpty(binusianId)) return;
            var rec = new RegistrationRecord { AnakKes = anakKes, ShiftId = shiftId, SavedAt = DateTime.Now };
            _store[binusianId] = rec;
        }
    }
}
