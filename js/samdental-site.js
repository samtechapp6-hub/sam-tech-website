"use strict";

(() => {
    const toggle = document.querySelector(".menu-toggle");
    const navigation = document.querySelector(".site-nav");
    if (toggle && navigation) {
        const closeMenu = () => {
            navigation.classList.remove("open");
            toggle.setAttribute("aria-expanded", "false");
            toggle.setAttribute("aria-label", "باز کردن منو");
        };

        toggle.addEventListener("click", () => {
            const isOpen = navigation.classList.toggle("open");
            toggle.setAttribute("aria-expanded", String(isOpen));
            toggle.setAttribute("aria-label", isOpen ? "بستن منو" : "باز کردن منو");
        });

        navigation.querySelectorAll("a").forEach(link => link.addEventListener("click", closeMenu));
        document.addEventListener("keydown", event => {
            if (event.key === "Escape") closeMenu();
        });
        document.addEventListener("click", event => {
            if (!navigation.contains(event.target) && !toggle.contains(event.target)) closeMenu();
        });
    }

    const heroSlider = document.querySelector("[data-hero-slider]");
    if (heroSlider) {
        const slides = [...heroSlider.querySelectorAll(".hero-slide")];
        const dots = [...heroSlider.querySelectorAll("[data-slide-to]")];
        const pauseButton = heroSlider.querySelector("[data-slide-toggle]");
        const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
        let currentSlide = 0;
        let timer = null;
        let userPaused = reducedMotion;
        let pointerPaused = false;
        let focusPaused = false;

        const showSlide = (nextIndex) => {
            currentSlide = (nextIndex + slides.length) % slides.length;
            slides.forEach((slide, index) => {
                const active = index === currentSlide;
                slide.classList.toggle("is-active", active);
                slide.setAttribute("aria-hidden", String(!active));
                if (active) {
                    const image = slide.querySelector("img");
                    if (image) image.loading = "eager";
                }
            });
            dots.forEach((dot, index) => dot.setAttribute("aria-current", String(index === currentSlide)));
        };

        const stopTimer = () => {
            if (timer !== null) window.clearInterval(timer);
            timer = null;
        };
        const startTimer = () => {
            stopTimer();
            if (!userPaused && !pointerPaused && !focusPaused && !document.hidden) timer = window.setInterval(() => showSlide(currentSlide + 1), 6500);
        };
        const updatePauseButton = () => {
            if (!pauseButton) return;
            pauseButton.textContent = userPaused ? "▶" : "Ⅱ";
            pauseButton.setAttribute("aria-pressed", String(userPaused));
            pauseButton.setAttribute("aria-label", userPaused ? "پخش خودکار اسلایدها" : "توقف نمایش خودکار");
        };

        heroSlider.querySelector("[data-slide-prev]")?.addEventListener("click", () => { showSlide(currentSlide - 1); startTimer(); });
        heroSlider.querySelector("[data-slide-next]")?.addEventListener("click", () => { showSlide(currentSlide + 1); startTimer(); });
        dots.forEach(dot => dot.addEventListener("click", () => { showSlide(Number(dot.dataset.slideTo)); startTimer(); }));
        pauseButton?.addEventListener("click", () => { userPaused = !userPaused; updatePauseButton(); startTimer(); });
        heroSlider.addEventListener("mouseenter", () => { pointerPaused = true; stopTimer(); });
        heroSlider.addEventListener("mouseleave", () => { pointerPaused = false; startTimer(); });
        heroSlider.addEventListener("focusin", () => { focusPaused = true; stopTimer(); });
        heroSlider.addEventListener("focusout", event => { if (!heroSlider.contains(event.relatedTarget)) { focusPaused = false; startTimer(); } });
        document.addEventListener("visibilitychange", startTimer);
        updatePauseButton();
        startTimer();
    }

    const offerBuilder = document.querySelector("#offerBuilder");
    if (offerBuilder) {
        const editionSummary = offerBuilder.querySelector("#offerSummaryEdition");
        const termSummary = offerBuilder.querySelector("#offerSummaryTerm");
        const addonsSummary = offerBuilder.querySelector("#offerSummaryAddons");

        const updateOfferSummary = () => {
            const edition = offerBuilder.querySelector('input[name="offerEdition"]:checked');
            const term = offerBuilder.querySelector('input[name="offerTerm"]:checked');
            const addons = [...offerBuilder.querySelectorAll('.add-on-option input[type="checkbox"]:checked')]
                .map(input => input.value);

            if (editionSummary && edition) editionSummary.textContent = edition.value;
            if (termSummary && term) termSummary.textContent = term.value;
            if (addonsSummary) addonsSummary.textContent = addons.length ? addons.join("، ") : "بدون گزینهٔ تکمیلی";

            offerBuilder.querySelectorAll(".edition-option, .term-option").forEach(label => {
                const input = label.querySelector("input");
                label.classList.toggle("is-selected", Boolean(input && input.checked));
            });
        };

        offerBuilder.addEventListener("change", updateOfferSummary);
        updateOfferSummary();
    }
})();
