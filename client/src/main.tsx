import {
  FluentProvider,
  Spinner,
  type Theme,
  webDarkTheme,
  webLightTheme,
} from "@fluentui/react-components";
import { QueryClientProvider, useQuery } from "@tanstack/react-query";
import {
  Component,
  type ErrorInfo,
  type ReactNode,
  StrictMode,
  useEffect,
  useRef,
  useState,
} from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter, Link, Route, Routes, useLocation } from "react-router";
import { api } from "./app/api";
import { Companies, InvitationPage } from "./app/companies";
import { Action, Feedback } from "./app/shared";
import { newQueryClient, Workspace } from "./app/workspace";
import "./styles.css";

const baseTheme = document.documentElement.dataset.theme === "dark" ? webDarkTheme : webLightTheme;
const theme: Theme = {
  ...baseTheme,
  fontFamilyBase: '"Segoe UI", Aptos, Calibri, -apple-system, BlinkMacSystemFont, sans-serif',
  borderRadiusMedium: "0.625rem",
  borderRadiusLarge: "16px",
  colorNeutralForeground1: "var(--cp-text)",
  colorNeutralForeground2: "var(--cp-text-muted)",
  colorNeutralForeground3: "var(--cp-text-soft)",
  colorNeutralForegroundDisabled: "var(--cp-text-soft)",
  colorNeutralBackground1: "var(--cp-surface)",
  colorNeutralBackground1Hover: "var(--cp-surface-soft)",
  colorNeutralBackground2: "var(--cp-surface-soft)",
  colorNeutralBackgroundDisabled: "var(--cp-surface-soft)",
  colorNeutralStroke1: "var(--cp-border)",
  colorNeutralStroke1Hover: "var(--cp-border-strong)",
  colorNeutralStrokeAccessible: "var(--cp-border-strong)",
  colorNeutralStrokeDisabled: "var(--cp-border)",
  colorBrandBackground: "var(--cp-accent)",
  colorBrandBackgroundHover: "var(--cp-accent-hover)",
  colorBrandBackgroundPressed: "var(--cp-accent-hover)",
  colorBrandForeground1: "var(--cp-accent)",
  colorNeutralForegroundOnBrand: "var(--cp-accent-fg)",
  colorStrokeFocus2: "var(--cp-accent)",
};

function App() {
  const { pathname } = useLocation();
  const main = useRef<HTMLElement>(null);
  useEffect(() => {
    document.title = pathname === "/invitations" ? "Join a company | Kanban PoC" : "Kanban PoC";
    main.current?.focus();
  }, [pathname]);
  const session = useQuery({
    queryKey: ["session"],
    queryFn: api.session,
    retry: false,
    refetchOnWindowFocus: true,
  });
  return (
    <div className="application">
      <header className="app-header">
        <Link className="brand" to="/">
          Kanban PoC
        </Link>
        <div className="header-account">
          <span className="scope-note">Synthetic data only</span>
          {session.data && (
            <>
              <span>{session.data.displayName}</span>
              <Action run={api.signOut}>Sign out</Action>
            </>
          )}
        </div>
      </header>
      <main className="content" ref={main} tabIndex={-1} aria-label="Kanban workspace">
        {session.isPending ? (
          <Spinner label="Checking your session" />
        ) : session.error ? (
          <Feedback error={session.error} />
        ) : (
          <SessionCache key={session.data?.identityKey ?? "signed-out"}>
            <Routes>
              <Route path="/invitations" element={<InvitationPage signedIn={!!session.data} />} />
              {session.data ? (
                <>
                  <Route path="/" element={<Companies identityKey={session.data.identityKey} />} />
                  <Route path="/companies/:companyId/*" element={<Workspace />} />
                </>
              ) : (
                <Route
                  path="*"
                  element={
                    <section className="identity-panel">
                      <h1>
                        A company for your team.
                        <br />A board for your work.
                      </h1>
                      <p>
                        Create your company, invite employees, and give each team the access it
                        needs. Sign-in is managed by Microsoft Entra External ID.
                      </p>
                      <div className="actions">
                        <a className="button-link" href="/auth/login">
                          Sign in or create an account
                        </a>
                      </div>
                      <p className="muted">
                        Synthetic data only. Company administration and team-content access are
                        separate.
                      </p>
                    </section>
                  }
                />
              )}
            </Routes>
          </SessionCache>
        )}
      </main>
    </div>
  );
}

function SessionCache({ children }: { children: ReactNode }) {
  const [cache] = useState(newQueryClient);
  useEffect(
    () => () => {
      void cache.cancelQueries();
      cache.clear();
    },
    [cache],
  );
  return <QueryClientProvider client={cache}>{children}</QueryClientProvider>;
}

const root = document.getElementById("root");
if (!root) throw new Error("The application root is missing.");
class ApplicationBoundary extends Component<{ children: ReactNode }, { error: Error | null }> {
  state: { error: Error | null } = { error: null };
  static getDerivedStateFromError(error: Error) {
    return { error };
  }
  componentDidCatch(_error: Error, _info: ErrorInfo) {
    /* Do not send drafts or invitation tokens to browser telemetry. */
  }
  render() {
    return this.state.error ? (
      <main className="content">
        <h1>This page could not be opened</h1>
        <Feedback error={this.state.error} />
        <p>
          Your saved creation attempt has not been retried or replaced. Reload the page after
          resolving browser storage or session problems.
        </p>
        <a href="/">Return to your companies</a>
      </main>
    ) : (
      this.props.children
    );
  }
}
const queryClient = newQueryClient();
createRoot(root).render(
  <StrictMode>
    <FluentProvider theme={theme}>
      <QueryClientProvider client={queryClient}>
        <BrowserRouter>
          <ApplicationBoundary>
            <App />
          </ApplicationBoundary>
        </BrowserRouter>
      </QueryClientProvider>
    </FluentProvider>
  </StrictMode>,
);
