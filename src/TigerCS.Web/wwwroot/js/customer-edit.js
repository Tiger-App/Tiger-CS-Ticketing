// Customers directory: the Edit modal. One native <dialog> serves every row - the Edit button copies the row's customer key, name and
// current phone / project / unit into it. A rejected edit comes back through data-reopen (the customer and what was typed), so the form
// opens again with the values and the error banner above explains. All validation lives in the Api; this only fills and shows the form.
(function () {
  "use strict";

  var dialog = document.querySelector("[data-customer-edit-dialog]");
  if (!dialog || typeof dialog.showModal !== "function") return;

  var key = dialog.querySelector("[data-edit-key]");
  var name = dialog.querySelector("[data-edit-name]");
  var phone = dialog.querySelector("[data-edit-phone]");
  var project = dialog.querySelector("[data-edit-project]");
  var unit = dialog.querySelector("[data-edit-unit]");

  function open(values) {
    key.value = values.key || "";
    name.textContent = values.name || "";
    phone.value = values.phone || "";
    project.value = values.project || "";
    unit.value = values.unit || "";
    dialog.showModal();
    phone.focus();
  }

  document.querySelectorAll("[data-customer-edit]").forEach(function (button) {
    button.addEventListener("click", function (event) {
      event.stopPropagation();   // the row itself is a link; the button is not
      open({
        key: button.getAttribute("data-key"),
        name: button.getAttribute("data-name"),
        phone: button.getAttribute("data-phone"),
        project: button.getAttribute("data-project"),
        unit: button.getAttribute("data-unit")
      });
    });
  });

  dialog.querySelectorAll("[data-edit-cancel]").forEach(function (button) {
    button.addEventListener("click", function () { dialog.close(); });
  });

  // Reopen a rejected edit with what was typed.
  var reopen = dialog.getAttribute("data-reopen");
  if (reopen) {
    try {
      var values = JSON.parse(reopen);
      var row = document.querySelector('[data-customer-edit][data-key="' + (values.key || "").replace(/"/g, '\\"') + '"]');
      open({
        key: values.key,
        name: row ? row.getAttribute("data-name") : "",
        phone: values.phone,
        project: values.project,
        unit: values.unit
      });
    } catch (e) { /* a malformed value just leaves the form closed */ }
  }
})();
