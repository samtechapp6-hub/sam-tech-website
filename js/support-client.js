"use strict";

(() => {
    const $ = (selector, root = document) => root.querySelector(selector);
    const authView = $("#authView");
    const portalView = $("#portalView");
    const notice = $("#supportNotice");
    let currentUser = null;
    let activeTicketNumber = null;

    async function api(path, options = {}) {
        const response = await fetch(path, {
            credentials: "same-origin",
            ...options,
            headers: {
                ...(options.body ? { "Content-Type": "application/json" } : {}),
                ...(options.headers || {})
            }
        });
        const data = response.status === 204 ? null : await response.json().catch(() => null);
        if (!response.ok) {
            const message = data?.message || (response.status === 401 ? "ایمیل یا گذرواژه درست نیست." : "ارتباط با سرور انجام نشد.");
            const error = new Error(message);
            error.status = response.status;
            throw error;
        }
        return data;
    }

    function showNotice(message, kind = "success") {
        notice.textContent = message;
        notice.dataset.kind = kind === "error" ? "error" : "success";
        notice.hidden = false;
        notice.scrollIntoView({ behavior: "smooth", block: "nearest" });
    }

    function clearNotice() {
        notice.hidden = true;
        notice.textContent = "";
    }

    function formObject(form) {
        return Object.fromEntries(new FormData(form).entries());
    }

    function formatDate(value) {
        return new Intl.DateTimeFormat("fa-IR", { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
    }

    function emptyState(text) {
        const element = document.createElement("p");
        element.className = "empty-state";
        element.textContent = text;
        return element;
    }

    function switchAuthTab(name) {
        document.querySelectorAll("[data-auth-tab]").forEach(button => {
            const active = button.dataset.authTab === name;
            button.classList.toggle("active", active);
            button.setAttribute("aria-selected", String(active));
        });
        $("#loginForm").hidden = name !== "login";
        $("#registerForm").hidden = name !== "register";
    }

    function renderPortal(user) {
        currentUser = user;
        authView.hidden = true;
        portalView.hidden = false;
        $("#portalUserName").textContent = user.fullName;
        $("#portalOrgName").textContent = user.organization || "حساب پشتیبانی سام‌تک";
        const isSupport = user.role === "SupportAdmin" || user.role === "SupportAgent";
        const isAdmin = user.role === "SupportAdmin";
        $("#portalRole").textContent = isSupport ? "پنل تیم پشتیبانی" : "پنل مشتریان سام‌تک";
        $("#registrationPanel").hidden = !isAdmin;
        $("#supportAgentsPanel").hidden = !isAdmin;
        $("#newTicketPanel").hidden = isSupport;
        $("#ticketsHeading").textContent = isSupport ? "همهٔ تیکت‌ها" : "تیکت‌های مرکز";
        $("#detailAdminControls").hidden = !isSupport;
        loadTickets();
        if (isAdmin) loadRegistrations();
    }

    async function restoreSession() {
        try {
            const result = await api("/api/support-portal/auth/me");
            renderPortal(result.user);
        } catch (error) {
            if (error.status !== 401) showNotice(error.message, "error");
            authView.hidden = false;
            portalView.hidden = true;
        }
    }

    async function loadTickets() {
        const list = $("#ticketList");
        list.replaceChildren(emptyState("در حال دریافت تیکت‌ها…"));
        try {
            const tickets = await api("/api/support-portal/tickets");
            list.replaceChildren();
            if (!tickets.length) {
                list.append(emptyState(currentUser?.role === "SupportAdmin" ? "هنوز تیکتی ثبت نشده است." : "هنوز تیکتی برای این مرکز ثبت نشده است."));
                return;
            }
            for (const ticket of tickets) {
                const button = document.createElement("button");
                button.type = "button";
                button.className = "ticket-card";
                const title = document.createElement("strong");
                title.textContent = `${ticket.ticketNumber} · ${ticket.subject}`;
                const meta = document.createElement("small");
                meta.textContent = currentUser?.role === "SupportAdmin"
                    ? `${ticket.organization} · ${ticket.product} · ${formatDate(ticket.updatedAtUtc)}`
                    : `${ticket.category} · ${ticket.priority} · ${formatDate(ticket.updatedAtUtc)}`;
                const status = document.createElement("span");
                status.className = "ticket-status";
                status.dataset.status = ticket.status;
                status.textContent = ticket.status;
                button.append(title, meta, status);
                button.addEventListener("click", () => openTicket(ticket.ticketNumber));
                list.append(button);
            }
        } catch (error) {
            list.replaceChildren(emptyState(error.message));
        }
    }

    async function loadRegistrations() {
        const list = $("#registrationsList");
        list.replaceChildren(emptyState("در حال دریافت درخواست‌ها…"));
        try {
            const registrations = await api("/api/support-portal/admin/registrations");
            list.replaceChildren();
            if (!registrations.length) {
                list.append(emptyState("درخواست حسابِ تأییدنشده‌ای وجود ندارد."));
                return;
            }
            for (const registration of registrations) {
                const card = document.createElement("div");
                card.className = "registration-card";
                const text = document.createElement("div");
                const title = document.createElement("strong");
                title.textContent = registration.organization;
                const meta = document.createElement("small");
                meta.textContent = `${registration.fullName} · ${registration.email} · ${registration.phone}`;
                text.append(title, meta);
                const activate = document.createElement("button");
                activate.type = "button";
                activate.textContent = "فعال‌سازی";
                activate.addEventListener("click", async () => {
                    activate.disabled = true;
                    try {
                        await api(`/api/support-portal/admin/users/${registration.userId}/activate`, { method: "POST" });
                        showNotice(`حساب «${registration.organization}» فعال شد.`);
                        loadRegistrations();
                    } catch (error) {
                        activate.disabled = false;
                        showNotice(error.message, "error");
                    }
                });
                card.append(text, activate);
                list.append(card);
            }
        } catch (error) {
            list.replaceChildren(emptyState(error.message));
        }
    }

    async function openTicket(ticketNumber) {
        try {
            const detail = await api(`/api/support-portal/tickets/${encodeURIComponent(ticketNumber)}`);
            activeTicketNumber = detail.ticket.ticketNumber;
            $("#detailNumber").textContent = detail.ticket.ticketNumber;
            $("#detailSubject").textContent = detail.ticket.subject;
            $("#detailMeta").textContent = `${detail.ticket.organization} · ${detail.ticket.product} · ${detail.ticket.category} · ${detail.ticket.priority} · ${detail.ticket.status}`;
            $("#detailStatus").value = detail.ticket.status;
            const thread = $("#messageThread");
            thread.replaceChildren();
            for (const message of detail.messages) {
                const bubble = document.createElement("article");
                bubble.className = `message-bubble${message.isSupport ? " is-support" : ""}`;
                const meta = document.createElement("div");
                meta.className = "message-meta";
                const sender = document.createElement("strong");
                sender.textContent = message.isSupport ? `پشتیبانی · ${message.senderName}` : message.senderName;
                const date = document.createElement("time");
                date.textContent = formatDate(message.createdAtUtc);
                meta.append(sender, date);
                const body = document.createElement("p");
                body.textContent = message.message;
                bubble.append(meta, body);
                thread.append(bubble);
            }
            const closed = detail.ticket.status === "بسته‌شده";
            $("#replyText").disabled = closed;
            $("#replyForm button").disabled = closed;
            $("#replyText").placeholder = closed ? "این تیکت بسته شده است." : "پاسختان را بنویسید…";
            $("#ticketDetail").hidden = false;
            $("#ticketDetail").scrollIntoView({ behavior: "smooth", block: "start" });
        } catch (error) {
            showNotice(error.message, "error");
        }
    }

    document.querySelectorAll("[data-auth-tab]").forEach(button => button.addEventListener("click", () => switchAuthTab(button.dataset.authTab)));

    $("#loginForm").addEventListener("submit", async event => {
        event.preventDefault();
        clearNotice();
        const button = event.currentTarget.querySelector("button[type=submit]");
        button.disabled = true;
        try {
            const result = await api("/api/support-portal/auth/login", { method: "POST", body: JSON.stringify(formObject(event.currentTarget)) });
            event.currentTarget.reset();
            renderPortal(result.user);
            showNotice("با موفقیت وارد شدید.");
        } catch (error) {
            showNotice(error.message, "error");
        } finally {
            button.disabled = false;
        }
    });

    $("#registerForm").addEventListener("submit", async event => {
        event.preventDefault();
        clearNotice();
        const data = formObject(event.currentTarget);
        const button = event.currentTarget.querySelector("button[type=submit]");
        button.disabled = true;
        try {
            const result = await api("/api/support-portal/auth/register", { method: "POST", body: JSON.stringify(data) });
            event.currentTarget.reset();
            switchAuthTab("login");
            $("#loginForm [name=email]").value = data.email;
            showNotice(result.message);
        } catch (error) {
            showNotice(error.message, "error");
        } finally {
            button.disabled = false;
        }
    });

    $("#agentForm").addEventListener("submit", async event => {
        event.preventDefault();
        const form = event.currentTarget;
        const button = form.querySelector("button[type=submit]");
        button.disabled = true;
        try {
            await api("/api/support-portal/admin/agents", { method: "POST", body: JSON.stringify(formObject(form)) });
            form.reset();
            showNotice("حساب همکار پشتیبانی ساخته شد.");
        } catch (error) {
            showNotice(error.message, "error");
        } finally {
            button.disabled = false;
        }
    });

    $("#newTicketForm").addEventListener("submit", async event => {
        event.preventDefault();
        clearNotice();
        const form = event.currentTarget;
        const button = form.querySelector("button[type=submit]");
        button.disabled = true;
        try {
            const ticket = await api("/api/support-portal/tickets", { method: "POST", body: JSON.stringify(formObject(form)) });
            form.reset();
            showNotice(`تیکت ${ticket.ticketNumber} ثبت شد.`);
            await loadTickets();
            openTicket(ticket.ticketNumber);
        } catch (error) {
            showNotice(error.message, "error");
        } finally {
            button.disabled = false;
        }
    });

    $("#replyForm").addEventListener("submit", async event => {
        event.preventDefault();
        if (!activeTicketNumber) return;
        const button = event.currentTarget.querySelector("button[type=submit]");
        button.disabled = true;
        try {
            await api(`/api/support-portal/tickets/${encodeURIComponent(activeTicketNumber)}/messages`, {
                method: "POST", body: JSON.stringify({ message: $("#replyText").value })
            });
            $("#replyText").value = "";
            showNotice("پاسخ ثبت شد.");
            await openTicket(activeTicketNumber);
            await loadTickets();
        } catch (error) {
            showNotice(error.message, "error");
        } finally {
            button.disabled = false;
        }
    });

    $("#saveStatusButton").addEventListener("click", async () => {
        if (!activeTicketNumber) return;
        try {
            await api(`/api/support-portal/admin/tickets/${encodeURIComponent(activeTicketNumber)}`, {
                method: "PATCH", body: JSON.stringify({ status: $("#detailStatus").value })
            });
            showNotice("وضعیت تیکت تغییر کرد.");
            await openTicket(activeTicketNumber);
            await loadTickets();
        } catch (error) {
            showNotice(error.message, "error");
        }
    });

    $("#refreshTicketsButton").addEventListener("click", loadTickets);
    $("#closeDetailButton").addEventListener("click", () => { $("#ticketDetail").hidden = true; activeTicketNumber = null; });
    $("#logoutButton").addEventListener("click", async () => {
        try { await api("/api/support-portal/auth/logout", { method: "POST" }); } catch { /* Show the sign-in page even if the session already expired. */ }
        currentUser = null;
        portalView.hidden = true;
        authView.hidden = false;
        $("#ticketDetail").hidden = true;
        switchAuthTab("login");
        showNotice("از حساب خارج شدید.");
    });

    restoreSession();
})();
