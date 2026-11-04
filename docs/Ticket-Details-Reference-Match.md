# Ticket Details — reference match

Restyle of the Ticket Details page to the approved Tiger Ticketing design. Backend data, forms, handlers, permissions and validation are unchanged.

Screenshots and the side-by-side comparison are in `docs/ticket-details-screenshots/reference-match/` (reference is a 1624px capture at 1:1, so the comparison is rendered at 1624; 1440, 1280 and 390 are captured separately). Pages were rendered through the real Razor pipeline (`TicketDetailsRedesignRenderTests`, scenario 219, with `TIGERCS_RENDER_DUMP_DIR`) and served with the app's own CSS/JS in Chromium.

Known differences from the reference:
* Bell icon: there is no notification feature, so none is drawn.
* "Contact: Customer contacted us" keeps the product wording instead of "Inbound".
* Customer name note / callout title is data-driven (verification label) rather than the mock's fixed text.
* Gold buttons are slightly darker than the mock so white labels stay >= 4.5:1.
* "Assigned To" keeps the product's capitalisation; the department is a muted second line; an extra "Why" link and "Times in UAE time" note stay for information the mock omits.
* Below 1560px the customer-row icons are dropped, and below 1400px it becomes a 2 x 2 grid.
