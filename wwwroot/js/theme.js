(() => {
    const root = document.documentElement;
    const themes = ["light", "dark", "rainbow"];
    const read = (key) => {
        try {
            return localStorage.getItem(key);
        } catch {
            return null;
        }
    };
    const save = (key, value) => {
        try {
            localStorage.setItem(key, value);
        } catch {
            // The picker still works when storage is unavailable.
        }
    };
    const savedTheme = read("proxycraft.theme");
    root.dataset.theme = themes.includes(savedTheme) ? savedTheme : "light";

    document.addEventListener("DOMContentLoaded", () => {
        const choices = document.querySelectorAll("[data-theme-choice]");
        const canvas = document.querySelector("#rainbow-particles");
        const context = canvas.getContext("2d");
        const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
        const pointer = { x: 0, y: 0, active: false };
        let animationFrame = null;
        let previousTime = 0;
        let animationTime = 0;
        let width = 0;
        let height = 0;
        let particles = [];

        function resize() {
            if (!context || (width === innerWidth && height === innerHeight)) {
                return;
            }
            width = innerWidth;
            height = innerHeight;
            const ratio = Math.min(devicePixelRatio || 1, 1.5);
            canvas.width = Math.round(width * ratio);
            canvas.height = Math.round(height * ratio);
            context.setTransform(ratio, 0, 0, ratio, 0, 0);
            const count = Math.min(80, Math.max(24, Math.floor(width * height / 16000)));
            particles = Array.from({ length: count }, () => ({
                x: Math.random() * width,
                y: Math.random() * height,
                vx: (Math.random() - 0.5) * 22,
                vy: (Math.random() - 0.5) * 22,
                size: Math.random() * 1.3 + 0.8,
                hue: Math.random() * 360
            }));
        }

        function draw(now, delta = 0) {
            if (!context) {
                return;
            }
            context.clearRect(0, 0, width, height);
            const hueShift = now / 100;
            if (pointer.active) {
                const glow = context.createRadialGradient(pointer.x, pointer.y, 0, pointer.x, pointer.y, 180);
                glow.addColorStop(0, `hsla(${hueShift}, 100%, 70%, 0.12)`);
                glow.addColorStop(1, "transparent");
                context.fillStyle = glow;
                context.fillRect(pointer.x - 180, pointer.y - 180, 360, 360);
            }
            for (const particle of particles) {
                particle.x = (particle.x + particle.vx * delta + width) % width;
                particle.y = (particle.y + particle.vy * delta + height) % height;
            }
            for (let index = 0; index < particles.length; index++) {
                const particle = particles[index];
                const hue = (particle.hue + hueShift) % 360;
                for (let next = index + 1; next < particles.length; next++) {
                    const neighbor = particles[next];
                    const distance = Math.hypot(particle.x - neighbor.x, particle.y - neighbor.y);
                    if (distance < 130) {
                        context.strokeStyle = `hsla(${hue}, 90%, 74%, ${(1 - distance / 130) * 0.22})`;
                        context.lineWidth = 0.65;
                        context.beginPath();
                        context.moveTo(particle.x, particle.y);
                        context.lineTo(neighbor.x, neighbor.y);
                        context.stroke();
                    }
                }
                if (pointer.active) {
                    const distance = Math.hypot(particle.x - pointer.x, particle.y - pointer.y);
                    if (distance < 230) {
                        const line = context.createLinearGradient(particle.x, particle.y, pointer.x, pointer.y);
                        line.addColorStop(0, `hsla(${hue}, 100%, 78%, ${(1 - distance / 230) * 0.9})`);
                        line.addColorStop(1, `hsla(${hue + 65}, 100%, 78%, 0.12)`);
                        context.strokeStyle = line;
                        context.lineWidth = 1;
                        context.beginPath();
                        context.moveTo(particle.x, particle.y);
                        context.lineTo(pointer.x, pointer.y);
                        context.stroke();
                    }
                }
                context.fillStyle = `hsla(${hue}, 100%, 80%, 0.85)`;
                context.beginPath();
                context.arc(particle.x, particle.y, particle.size, 0, Math.PI * 2);
                context.fill();
            }
            if (pointer.active) {
                context.strokeStyle = `hsla(${hueShift + 150}, 100%, 82%, 0.8)`;
                context.lineWidth = 1;
                context.beginPath();
                context.arc(pointer.x, pointer.y, 6, 0, Math.PI * 2);
                context.stroke();
            }
        }

        function frame(now) {
            animationFrame = requestAnimationFrame(frame);
            if (now - previousTime < 1000 / 30) {
                return;
            }
            const delta = previousTime ? Math.min((now - previousTime) / 1000, 0.05) : 0;
            previousTime = now;
            animationTime += delta * 1000;
            draw(animationTime, delta);
        }

        function synchronize() {
            if (animationFrame !== null) {
                cancelAnimationFrame(animationFrame);
            }
            animationFrame = null;
            previousTime = 0;
            pointer.active = false;
            const rainbow = root.dataset.theme === "rainbow";
            const animate = rainbow && !reducedMotion.matches && !!context;
            root.dataset.effects = animate && !document.hidden ? "on" : "off";
            for (const choice of choices) {
                choice.setAttribute("aria-pressed", String(choice.dataset.themeChoice === root.dataset.theme));
            }
            if (rainbow) {
                resize();
                draw(animationTime);
                if (animate && !document.hidden) {
                    animationFrame = requestAnimationFrame(frame);
                }
            } else if (context) {
                context.clearRect(0, 0, width, height);
            }
        }

        for (const choice of choices) {
            choice.addEventListener("click", () => {
                root.dataset.theme = choice.dataset.themeChoice;
                save("proxycraft.theme", root.dataset.theme);
                synchronize();
            });
        }
        document.addEventListener("pointermove", (event) => {
            if (root.dataset.theme !== "rainbow" || root.dataset.effects !== "on" || event.pointerType === "touch") {
                return;
            }
            pointer.x = event.clientX;
            pointer.y = event.clientY;
            pointer.active = true;
        }, { passive: true });
        document.addEventListener("pointerleave", () => { pointer.active = false; });
        window.addEventListener("blur", () => { pointer.active = false; });
        window.addEventListener("resize", () => {
            if (root.dataset.theme === "rainbow") {
                resize();
                draw(animationTime);
            }
        });
        document.addEventListener("visibilitychange", synchronize);
        reducedMotion.addEventListener("change", synchronize);
        window.addEventListener("pageshow", () => {
            const saved = read("proxycraft.theme");
            if (themes.includes(saved)) {
                root.dataset.theme = saved;
            }
            synchronize();
        });
        synchronize();
    }, { once: true });
})();
