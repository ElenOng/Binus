using Binus.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Binus.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }

        public IActionResult Privacy()
        {
            return View();
        }

        private readonly Binus.DataAccess.IRegistrationRepository _registrationRepository;

        public HomeController(Binus.DataAccess.IRegistrationRepository registrationRepository)
        {
            _registrationRepository = registrationRepository;
        }

        // Registration period - for demo we set a period around today
        private readonly DateTime _regStart = DateTime.Today.AddDays(-1);
        private readonly DateTime _regEnd = DateTime.Today.AddDays(7);

        [Authorize]
        [HttpGet]
        public async Task<IActionResult> Registration()
        {
            var model = new RegistrationViewModel();
            var binusianId = User.FindFirst("BinusianId")?.Value;
            if (!string.IsNullOrEmpty(binusianId))
            {
                var children = await _registrationRepository.GetChildrenAsync(binusianId);
                model.Children = children.Select(c => new ChildOption { AnakKe = c.AnakKe, Name = c.Nama, Age = c.TanggalLahir.HasValue ? (DateTime.Today.Year - c.TanggalLahir.Value.Year) - (c.TanggalLahir.Value.Date > DateTime.Today.AddYears(-(DateTime.Today.Year - c.TanggalLahir.Value.Year)) ? 1 : 0) : 0 }).ToList();
                // Age is not stored in master_data_anak_pegawai in diagram; set 0 or derive if available

                var shifts = await _registrationRepository.GetShiftsAsync();
                model.Shifts = shifts.Select(s => new ShiftOption { ShiftId = s.ShiftId, Info = s.ShiftInfo, Quota = s.Quota }).ToList();

                // Prefill of saved registration is omitted here to keep the repository surface minimal.
                // If you want prefill, add a repository method to read transaction_registration for the binusian.
            }

            model.IsRegistrationOpen = DateTime.Now >= _regStart && DateTime.Now <= _regEnd;
            return View(model);
        }

        [Authorize]
        [HttpPost]
        public async Task<JsonResult> CheckConflicts([FromForm] int shiftId, [FromForm] int[] anakKes)
        {
            var binusianId = User.FindFirst("BinusianId")?.Value ?? string.Empty;
            var conflicts = await _registrationRepository.GetRegisteredAnakKesAsync(shiftId, anakKes ?? new int[0], binusianId);
            return Json(new { conflicts });
        }

        [Authorize]
        [HttpPost]
        public async Task<IActionResult> Registration(RegistrationViewModel model)
        {
            // require at least one child and a shift
            if (model.SelectedChildren == null || !model.SelectedChildren.Any())
            {
                ModelState.AddModelError(string.Empty, "Please select at least one child.");
            }

            if (!model.SelectedShiftId.HasValue || model.SelectedShiftId.Value == 0)
            {
                ModelState.AddModelError(string.Empty, "Please select a shift.");
            }

            // server-side validation: max 3 children
            if (model.SelectedChildren == null) model.SelectedChildren = new List<int>();
            if (model.SelectedChildren.Count > 3)
            {
                ModelState.AddModelError(string.Empty, "Maximum 3 children can be selected.");
            }

            // validate ages using DB data
            var binusianIdForCheck = User.FindFirst("BinusianId")?.Value;
            var childrenFromDb = new List<(int AnakKe, string Nama, System.DateTime? TanggalLahir)>();
            if (!string.IsNullOrEmpty(binusianIdForCheck))
            {
                childrenFromDb = await _registrationRepository.GetChildrenAsync(binusianIdForCheck);
            }

            bool validAges = true;
            foreach (var child in childrenFromDb.Where(c => model.SelectedChildren.Contains(c.AnakKe)))
            {
                if (child.TanggalLahir == null)
                {
                    validAges = false; break;
                }
                var age = DateTime.Today.Year - child.TanggalLahir.Value.Year;
                if (child.TanggalLahir.Value.Date > DateTime.Today.AddYears(-age)) age--;
                if (age < 2 || age > 21) { validAges = false; break; }
            }
            if (!validAges)
            {
                ModelState.AddModelError(string.Empty, "Selected child(ren) must be between 2 and 21 years old.");
            }

            // check registration period
            var isOpen = DateTime.Now >= _regStart && DateTime.Now <= _regEnd;
            if (!isOpen)
            {
                ModelState.AddModelError(string.Empty, "Registration period is closed.");
            }

            if (!ModelState.IsValid)
            {
                // reload lists from DB
                var binusianId = User.FindFirst("BinusianId")?.Value;
                if (!string.IsNullOrEmpty(binusianId))
                {
                    var children = await _registrationRepository.GetChildrenAsync(binusianId);
                    model.Children = children.Select(c => new ChildOption { AnakKe = c.AnakKe, Name = c.Nama, Age = c.TanggalLahir.HasValue ? (DateTime.Today.Year - c.TanggalLahir.Value.Year) - (c.TanggalLahir.Value.Date > DateTime.Today.AddYears(-(DateTime.Today.Year - c.TanggalLahir.Value.Year)) ? 1 : 0) : 0 }).ToList();
                    var shifts = await _registrationRepository.GetShiftsAsync();
                    model.Shifts = shifts.Select(s => new ShiftOption { ShiftId = s.ShiftId, Info = s.ShiftInfo, Quota = s.Quota }).ToList();
                }
                model.IsRegistrationOpen = isOpen;
                return View(model);
            }
            var binusianIdFinal = User.FindFirst("BinusianId")?.Value ?? string.Empty;
            var shiftId = model.SelectedShiftId ?? 0;
            var result = await _registrationRepository.SaveRegistrationAsync(binusianIdFinal, model.SelectedChildren, shiftId);
            if (result != 0)
            {
                if (result == 1)
                    ModelState.AddModelError(string.Empty, "Not enough quota for the selected shift.");
                else if (result == 2)
                    ModelState.AddModelError(string.Empty, "One or more selected child(ren) are already registered for the selected shift.");
                else if (result == 3)
                    ModelState.AddModelError(string.Empty, "Data already exists (no changes were made).");

                // reload lists
                var children = await _registrationRepository.GetChildrenAsync(binusianIdFinal);
                model.Children = children.Select(c => new ChildOption { AnakKe = c.AnakKe, Name = c.Nama, Age = c.TanggalLahir.HasValue ? (DateTime.Today.Year - c.TanggalLahir.Value.Year) - (c.TanggalLahir.Value.Date > DateTime.Today.AddYears(-(DateTime.Today.Year - c.TanggalLahir.Value.Year)) ? 1 : 0) : 0 }).ToList();
                var shifts = await _registrationRepository.GetShiftsAsync();
                model.Shifts = shifts.Select(s => new ShiftOption { ShiftId = s.ShiftId, Info = s.ShiftInfo, Quota = s.Quota }).ToList();
                model.IsRegistrationOpen = isOpen;
                return View(model);
            }

            // reload state after successful save
            var savedChildren = await _registrationRepository.GetChildrenAsync(binusianIdFinal);
            model.Children = savedChildren.Select(c => new ChildOption { AnakKe = c.AnakKe, Name = c.Nama, Age = c.TanggalLahir.HasValue ? (DateTime.Today.Year - c.TanggalLahir.Value.Year) - (c.TanggalLahir.Value.Date > DateTime.Today.AddYears(-(DateTime.Today.Year - c.TanggalLahir.Value.Year)) ? 1 : 0) : 0 }).ToList();
            var newShifts = await _registrationRepository.GetShiftsAsync();
            model.Shifts = newShifts.Select(s => new ShiftOption { ShiftId = s.ShiftId, Info = s.ShiftInfo, Quota = s.Quota }).ToList();
            model.SavedAt = DateTime.Now;
            model.IsRegistrationOpen = isOpen;
            ViewBag.Message = "Data already saved";
            return View(model);
        }

        // Demo Build is removed; data now comes from the IRegistrationRepository

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
