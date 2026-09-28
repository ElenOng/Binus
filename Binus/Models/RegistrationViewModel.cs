using System;
using System.Collections.Generic;

namespace Binus.Models
{
    public class RegistrationViewModel
    {
        public List<ChildOption> Children { get; set; } = new();
        public List<ShiftOption> Shifts { get; set; } = new();

        // Selected child ids (anak_ke)
        public List<int> SelectedChildren { get; set; } = new();

        // Selected shift id
        public int? SelectedShiftId { get; set; }

        public DateTime? SavedAt { get; set; }

        public bool IsRegistrationOpen { get; set; }
    }
}
