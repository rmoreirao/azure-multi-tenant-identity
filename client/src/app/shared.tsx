import { Button, MessageBar, MessageBarBody, Spinner } from "@fluentui/react-components";
import { useInfiniteQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { type FormEvent, type ReactNode, useEffect, useRef, useState } from "react";
import { ApiError, type Page } from "./api";

export const roles = ["No team access", "Viewer", "Member", "Team manager"];
export const columns = [
  ["todo", "To do"],
  ["inProgress", "In progress"],
  ["done", "Done"],
] as const;
export const field = (data: FormData, name: string) => String(data.get(name) ?? "");
export function Feedback({ error }: { error: Error | null }) {
  if (!error) return null;
  return (
    <MessageBar intent="error">
      <MessageBarBody>
        {error.message}{" "}
        {error instanceof ApiError &&
          error.status === 412 &&
          "Your draft is unchanged. Review the current record before submitting again."}
        {error instanceof ApiError &&
          error.retryAfter &&
          ` Retry after ${error.retryAfter} seconds.`}
        {error instanceof ApiError && error.status === 401 && <a href="/auth/login">Sign in</a>}
      </MessageBarBody>
    </MessageBar>
  );
}
export function usePages<T>(key: readonly unknown[], load: (cursor?: string) => Promise<Page<T>>) {
  const query = useInfiniteQuery({
    queryKey: key,
    queryFn: ({ pageParam }) => load(pageParam),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    retry: false,
  });
  return { ...query, items: query.data?.pages.flatMap((p) => p.items) ?? [] };
}
export function PageState({
  query,
}: {
  query: {
    isPending: boolean;
    error: Error | null;
    hasNextPage?: boolean;
    isFetchingNextPage?: boolean;
    fetchNextPage?: () => unknown;
  };
}) {
  return (
    <>
      <Feedback error={query.error} />
      {query.isPending && <Spinner label="Loading" />}
      {query.hasNextPage && (
        <Button disabled={query.isFetchingNextPage} onClick={() => query.fetchNextPage?.()}>
          Load more
        </Button>
      )}
    </>
  );
}
export function MutationForm({
  children,
  submit,
  label,
  reset = true,
  success,
  version,
  reload,
  disabled = false,
}: {
  children: ReactNode;
  submit: (data: FormData) => Promise<unknown>;
  label: string;
  reset?: boolean;
  success?: () => void;
  version?: string;
  reload?: () => void;
  disabled?: boolean;
}) {
  const client = useQueryClient();
  const mutation = useMutation({
    mutationFn: submit,
    onSuccess: async () => {
      await client.invalidateQueries();
      success?.();
    },
  });
  const [dirty, setDirty] = useState(false);
  const [loadedVersion, setLoadedVersion] = useState(version);
  const formRef = useRef<HTMLFormElement>(null);
  const stale = version !== loadedVersion;
  useEffect(() => {
    if (!dirty) return;
    const unload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
    };
    const leave = (event: MouseEvent) => {
      if (
        event.target instanceof Element &&
        event.target.closest("a[href]") &&
        !window.confirm("Leave this page and discard your unsaved draft?")
      )
        event.preventDefault();
    };
    window.addEventListener("beforeunload", unload);
    document.addEventListener("click", leave, true);
    return () => {
      window.removeEventListener("beforeunload", unload);
      document.removeEventListener("click", leave, true);
    };
  }, [dirty]);
  function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (stale) return;
    const form = event.currentTarget;
    mutation.mutate(new FormData(form), {
      onSuccess: (value) => {
        setDirty(false);
        if (
          version !== undefined &&
          value &&
          typeof value === "object" &&
          "version" in value &&
          typeof value.version === "string"
        )
          setLoadedVersion(value.version);
        if (reset) form.reset();
      },
    });
  }
  return (
    <form ref={formRef} className="editor-form" onSubmit={onSubmit} onChange={() => setDirty(true)}>
      <fieldset disabled={mutation.isPending || disabled}>{children}</fieldset>
      <Feedback error={mutation.error} />
      {stale && (
        <MessageBar intent="warning">
          <MessageBarBody>
            This record changed. Your draft is preserved.{" "}
            <Button
              onClick={() => {
                if (!dirty || window.confirm("Discard this draft and reload the current record?")) {
                  formRef.current?.reset();
                  reload?.();
                  setLoadedVersion(version);
                  setDirty(false);
                }
              }}
            >
              Reload form
            </Button>
          </MessageBarBody>
        </MessageBar>
      )}
      <div className="form-actions">
        <Button
          type="submit"
          appearance="primary"
          disabled={mutation.isPending || stale || disabled}
        >
          {mutation.isPending ? "Saving..." : label}
        </Button>
        {mutation.isSuccess && <span role="status">Saved.</span>}
      </div>
    </form>
  );
}
export function Action({
  run,
  children,
  confirm,
}: {
  run: () => Promise<unknown>;
  children: ReactNode;
  confirm?: string;
}) {
  const client = useQueryClient();
  const mutation = useMutation({ mutationFn: run, onSuccess: () => client.invalidateQueries() });
  return (
    <div className="inline-action">
      <Button
        disabled={mutation.isPending}
        onClick={() => {
          if (!confirm || window.confirm(confirm)) mutation.mutate();
        }}
      >
        {mutation.isPending ? "Saving..." : children}
      </Button>
      <Feedback error={mutation.error} />
    </div>
  );
}
export function Empty({ children }: { children: ReactNode }) {
  return <p className="empty-state">{children}</p>;
}
