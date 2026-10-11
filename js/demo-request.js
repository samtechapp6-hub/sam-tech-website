"use strict";

(() => {
    const form = document.getElementById("demoRequestForm");
    const question = document.getElementById("demoCaptchaQuestion");
    const answer = document.getElementById("demoCaptchaAnswer");
    const status = document.getElementById("demoFormStatus");
    if (!form || !question || !answer || !status) return;

    let expectedAnswer = 0;
    const toEnglishDigits = value => String(value ?? "")
        .replace(/[۰-۹]/g, digit => String("۰۱۲۳۴۵۶۷۸۹".indexOf(digit)))
        .replace(/[٠-٩]/g, digit => String("٠١٢٣٤٥٦٧٨٩".indexOf(digit)))
        .trim();

    const newChallenge = () => {
        const left = Math.floor(Math.random() * 5) + 2;
        const right = Math.floor(Math.random() * 5) + 2;
        expectedAnswer = left + right;
        question.textContent = `${left} + ${right} = ؟`;
        answer.value = "";
    };

    const showStatus = (message, kind) => {
        status.textContent = message;
        status.classList.remove("is-error", "is-notice", "is-success");
        if (kind) status.classList.add(`is-${kind}`);
    };

    newChallenge();
    form.addEventListener("submit", event => {
        event.preventDefault();
        showStatus("", null);

        if (!form.reportValidity()) return;
        if (form.elements.website.value.trim()) return;

        const phone = toEnglishDigits(form.elements.phone.value).replace(/[\s-]/g, "");
        const normalizedPhone = phone.startsWith("+98") ? `0${phone.slice(3)}` : phone;
        if (!/^09\d{9}$/.test(normalizedPhone)) {
            form.elements.phone.setCustomValidity("شماره همراه را به شکل ۰۹۱۲۱۲۳۴۵۶۷ وارد کنید.");
            form.elements.phone.reportValidity();
            form.elements.phone.setCustomValidity("");
            return;
        }

        if (Number(toEnglishDigits(answer.value)) !== expectedAnswer) {
            showStatus("پاسخ عبارت امنیتی درست نیست؛ دوباره تلاش کنید.", "error");
            answer.focus();
            newChallenge();
            return;
        }

        showStatus("اطلاعات فرم بررسی شد. ثبت و پیگیری آنلاین پس از اتصال فرم به پشتیبانی فعال می‌شود.", "notice");
        newChallenge();
    });
})();
