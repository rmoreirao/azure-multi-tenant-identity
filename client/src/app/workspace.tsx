import { QueryClient, QueryClientProvider, useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Link, NavLink, Route, Routes, useParams } from "react-router";
import { api, type Model } from "./api";
import { Board, TaskDetail, Teams } from "./board";
import { Setup } from "./companies";
import { Approvals, Audit, Connections } from "./connections";
import { Directory, Groups, TeamAccess, TeamAdministration } from "./directory";
import { Empty, PageState } from "./shared";

export function newQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, refetchOnWindowFocus: false, staleTime: 15000 },
      mutations: { retry: false },
    },
  });
}
export function Workspace() {
  const { companyId = "" } = useParams();
  return <CompanyCache key={companyId} companyId={companyId} />;
}
function CompanyCache({ companyId }: { companyId: string }) {
  const [cache] = useState(newQueryClient);
  useEffect(
    () => () => {
      void cache.cancelQueries();
      cache.clear();
    },
    [cache],
  );
  return (
    <QueryClientProvider client={cache}>
      <CompanyWorkspace companyId={companyId} />
    </QueryClientProvider>
  );
}
function CompanyWorkspace({ companyId }: { companyId: string }) {
  const company = useQuery({
    queryKey: ["company", companyId],
    queryFn: () => api.company(companyId),
  });
  if (!company.data || company.error)
    return (
      <>
        <Link to="/">Your companies</Link>
        <PageState query={company} />
      </>
    );
  return (
    <div className="workspace">
      <aside className="workspace-nav">
        <Link className="company-context" to={`/companies/${companyId}`}>
          {company.data.name}
        </Link>
        <Link className="switch-company" to="/">
          Switch company
        </Link>
        <nav aria-label="Company navigation">
          <NavLink end to={`/companies/${companyId}`}>
            Team boards
          </NavLink>
          <NavLink to={`/companies/${companyId}/connections`}>Your connections</NavLink>
          <NavLink to={`/companies/${companyId}/audit`}>Audit events</NavLink>
          {company.data.administrator && (
            <>
              <span className="nav-section">Company administration</span>
              <NavLink to={`/companies/${companyId}/setup`}>Company setup</NavLink>
              <NavLink to={`/companies/${companyId}/directory`}>Directory & invitations</NavLink>
              <NavLink to={`/companies/${companyId}/groups`}>Groups</NavLink>
              <NavLink to={`/companies/${companyId}/team-administration`}>Team recovery</NavLink>
              <NavLink to={`/companies/${companyId}/approvals`}>Client approvals</NavLink>
            </>
          )}
        </nav>
      </aside>
      <div className="workspace-content">
        <Routes>
          <Route index element={<Teams company={company.data} />} />
          <Route path="connections" element={<Connections companyId={companyId} />} />
          <Route path="audit" element={<Audit company={company.data} />} />
          <Route path="teams/:teamId" element={<BoardRoute company={company.data} />} />
          <Route
            path="teams/:teamId/tasks/:taskId"
            element={<TaskDetail company={company.data} />}
          />
          <Route path="teams/:teamId/access" element={<AccessRoute companyId={companyId} />} />
          <Route
            path="setup"
            element={
              <Admin company={company.data}>
                <Setup company={company.data} />
              </Admin>
            }
          />
          <Route
            path="directory"
            element={
              <Admin company={company.data}>
                <Directory company={company.data} />
              </Admin>
            }
          />
          <Route
            path="groups"
            element={
              <Admin company={company.data}>
                <Groups companyId={companyId} />
              </Admin>
            }
          />
          <Route
            path="team-administration"
            element={
              <Admin company={company.data}>
                <TeamAdministration companyId={companyId} />
              </Admin>
            }
          />
          <Route
            path="approvals"
            element={
              <Admin company={company.data}>
                <Approvals companyId={companyId} />
              </Admin>
            }
          />
          <Route
            path="*"
            element={
              <Empty>
                This company page does not exist.{" "}
                <Link to={`/companies/${companyId}`}>Go to team boards.</Link>
              </Empty>
            }
          />
        </Routes>
      </div>
    </div>
  );
}
function BoardRoute({ company }: { company: Model<"CompanyView"> }) {
  const { teamId = "" } = useParams();
  return <Board key={teamId} company={company} />;
}
function AccessRoute({ companyId }: { companyId: string }) {
  const { teamId = "" } = useParams();
  return (
    <>
      <Link to={`/companies/${companyId}/teams/${teamId}`}>Back to board</Link>
      <TeamAccess key={teamId} companyId={companyId} teamId={teamId} />
    </>
  );
}
function Admin({
  company,
  children,
}: {
  company: Model<"CompanyView">;
  children: React.ReactNode;
}) {
  return company.administrator ? (
    children
  ) : (
    <Empty>This page requires company administrator access.</Empty>
  );
}
