"use strict";

(() => {
    const showcase = document.querySelector("[data-module-showcase]");
    if (!showcase) return;

    const tabs = [...showcase.querySelectorAll("[role='tab'][data-module]")];
    const panel = showcase.querySelector("[role='tabpanel']");
    const title = showcase.querySelector("[data-module-title]");
    const description = showcase.querySelector("[data-module-description]");
    const tag = showcase.querySelector("[data-module-tag]");
    const imageLabel = showcase.querySelector("[data-module-image-label]");
    const preview = showcase.querySelector("[data-module-preview]");
    const placeholder = showcase.querySelector(".module-image-placeholder");
    if (!tabs.length || !panel || !title || !description || !tag || !imageLabel) return;

    let imageRequest = 0;
    const imageSources = {
        dashboard: "images/modules/dashboard.png",
        patient: "images/modules/patient.png",
        imaging: "images/modules/imaging.png",
        chart: "images/modules/chart.png",
        laboratory: "images/modules/laboratory.png",
        reminders: "images/modules/reminders.png",
        insurance: "images/modules/insurance.png",
        appointments: "images/modules/appointments.png",
        sms: "images/modules/sms.png",
        inventory: "images/modules/inventory.png",
        reports: "images/modules/reports.png"
    };

    const selectTab = tab => {
        tabs.forEach(item => {
            const active = item === tab;
            item.classList.toggle("is-active", active);
            item.setAttribute("aria-selected", String(active));
            item.tabIndex = active ? 0 : -1;
        });

        title.textContent = tab.dataset.title;
        description.textContent = tab.dataset.description;
        tag.textContent = tab.dataset.tag;
        imageLabel.textContent = tab.dataset.imageLabel;
        if (preview && placeholder) {
            const request = ++imageRequest;
            preview.hidden = true;
            placeholder.hidden = false;
            preview.onload = () => {
                if (request !== imageRequest) return;
                preview.hidden = false;
                placeholder.hidden = true;
            };
            preview.onerror = () => {
                if (request !== imageRequest) return;
                preview.hidden = true;
                placeholder.hidden = false;
            };
            preview.src = imageSources[tab.dataset.module] || "";
        }
        panel.setAttribute("aria-labelledby", tab.id);
        panel.classList.remove("is-changing");
        void panel.offsetWidth;
        panel.classList.add("is-changing");
    };

    tabs.forEach((tab, index) => {
        tab.addEventListener("click", () => selectTab(tab));
        tab.addEventListener("keydown", event => {
            let nextIndex = index;
            if (event.key === "ArrowLeft") nextIndex = (index + 1) % tabs.length;
            else if (event.key === "ArrowRight") nextIndex = (index - 1 + tabs.length) % tabs.length;
            else if (event.key === "Home") nextIndex = 0;
            else if (event.key === "End") nextIndex = tabs.length - 1;
            else return;

            event.preventDefault();
            tabs[nextIndex].focus();
            selectTab(tabs[nextIndex]);
        });
    });

    const initialTab = tabs.find(tab => tab.getAttribute("aria-selected") === "true") || tabs[0];
    tabs.forEach(tab => { tab.tabIndex = tab === initialTab ? 0 : -1; });
    selectTab(initialTab);

    const wireImagePreview = (previewSelector, placeholderSelector) => {
        const image = document.querySelector(previewSelector);
        const imagePlaceholder = document.querySelector(placeholderSelector);
        if (!image || !imagePlaceholder) return;
        const showImage = () => {
            image.hidden = false;
            imagePlaceholder.hidden = true;
        };
        if (image.complete && image.naturalWidth > 0) showImage();
        else {
            image.addEventListener("load", showImage, { once: true });
            image.addEventListener("error", () => {
                image.hidden = true;
                imagePlaceholder.hidden = false;
            }, { once: true });
        }
    };

    wireImagePreview("[data-accounting-preview]", ".accounting-image-placeholder");
    wireImagePreview("[data-crm-preview]", ".crm-image-placeholder");
    wireImagePreview("[data-login-preview]", ".login-image-placeholder");
    wireImagePreview("[data-nfc-preview]", ".nfc-image-placeholder");
})();
