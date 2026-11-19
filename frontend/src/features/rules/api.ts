import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { apiFetch } from '@/lib/apiClient'

/**
 * Ported verbatim from frontend/src/features/settings/rules/api.ts (v1) --
 * request/response contracts mirroring 04-05's CategoryRule CRUD/test/
 * apply-now endpoints exactly (see 04-08-PLAN.md's <interfaces> block) --
 * no `any`, so a backend contract change surfaces here as a type error.
 * Follows connections/api.ts's accessToken-per-call TanStack Query v5
 * pattern: one hook per endpoint, mutations invalidate ['category-rules']
 * on success. Only the `apiFetch` import path changed.
 *
 * Create/update/delete/apply-now ALSO invalidate ['transactions'] on
 * success -- a rule change can affect what the feed shows via
 * CategorySource, matching 03-08's own cross-invalidation precedent for
 * its mutations.
 */

export type RuleMatchField = 'DESCRIPTION' | 'MERCHANT' | 'PLUGGY_CATEGORY' | 'AMOUNT'
export type RuleMatchOperator = 'CONTAINS' | 'EQUALS' | 'REGEX' | 'RANGE'

export interface CategoryRuleResponse {
  id: string
  matchField: RuleMatchField
  matchOperator: RuleMatchOperator
  matchValue: string | null
  amountMin: number | null
  amountMax: number | null
  categoryId: string
  categoryName: string
  priority: number
  isActive: boolean
  markAsTransfer: boolean
}

export interface CategoryRuleWriteFields {
  matchField: RuleMatchField
  matchOperator: RuleMatchOperator
  matchValue?: string | null
  amountMin?: number | null
  amountMax?: number | null
  categoryId: string
  priority?: number
  isActive: boolean
  markAsTransfer?: boolean
}

export interface CreateCategoryRuleRequest extends CategoryRuleWriteFields {
  accessToken: string
}

export interface UpdateCategoryRuleRequest extends CategoryRuleWriteFields {
  accessToken: string
  id: string
}

export interface ToggleCategoryRuleActiveRequest {
  accessToken: string
  id: string
  isActive: boolean
}

export interface ReorderCategoryRulesRequest {
  accessToken: string
  orderedIds: string[]
}

export interface DeleteCategoryRuleRequest {
  accessToken: string
  id: string
}

export interface TestCategoryRuleRequest {
  accessToken: string
  matchField: RuleMatchField
  matchOperator: RuleMatchOperator
  matchValue?: string | null
  amountMin?: number | null
  amountMax?: number | null
}

export interface TestCategoryRuleSampleRow {
  id: string
  date: string
  description: string
  merchantName: string | null
  amount: number
}

export interface TestCategoryRuleResponse {
  count: number
  sample: TestCategoryRuleSampleRow[]
}

export interface ApplyCategoryRuleNowRequest {
  accessToken: string
  id: string
}

export interface ApplyCategoryRuleNowResponse {
  updatedCount: number
}

export function useCategoryRulesQuery(accessToken: string) {
  return useQuery({
    queryKey: ['category-rules'],
    queryFn: () => apiFetch<CategoryRuleResponse[]>('/api/category-rules', { accessToken }),
    enabled: !!accessToken,
  })
}

function invalidateRuleAndTransactionQueries(queryClient: ReturnType<typeof useQueryClient>) {
  void queryClient.invalidateQueries({ queryKey: ['category-rules'] })
  void queryClient.invalidateQueries({ queryKey: ['transactions'] })
}

export function useCreateCategoryRuleMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, ...fields }: CreateCategoryRuleRequest) =>
      apiFetch<CategoryRuleResponse>('/api/category-rules', {
        method: 'POST',
        body: fields,
        accessToken,
      }),
    onSuccess: () => invalidateRuleAndTransactionQueries(queryClient),
  })
}

export function useUpdateCategoryRuleMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, ...fields }: UpdateCategoryRuleRequest) =>
      apiFetch<CategoryRuleResponse>(`/api/category-rules/${id}`, {
        method: 'PUT',
        body: fields,
        accessToken,
      }),
    onSuccess: () => invalidateRuleAndTransactionQueries(queryClient),
  })
}

export function useToggleCategoryRuleActiveMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, isActive }: ToggleCategoryRuleActiveRequest) =>
      apiFetch<CategoryRuleResponse>(`/api/category-rules/${id}/active`, {
        method: 'PATCH',
        body: { isActive },
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['category-rules'] })
    },
  })
}

export function useReorderCategoryRulesMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, orderedIds }: ReorderCategoryRulesRequest) =>
      apiFetch<CategoryRuleResponse[]>('/api/category-rules/reorder', {
        method: 'PUT',
        body: { orderedIds },
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['category-rules'] })
    },
  })
}

export function useDeleteCategoryRuleMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id }: DeleteCategoryRuleRequest) =>
      apiFetch<void>(`/api/category-rules/${id}`, {
        method: 'DELETE',
        accessToken,
      }),
    onSuccess: () => invalidateRuleAndTransactionQueries(queryClient),
  })
}

export function useTestCategoryRuleMutation() {
  return useMutation({
    mutationFn: ({ accessToken, ...fields }: TestCategoryRuleRequest) =>
      apiFetch<TestCategoryRuleResponse>('/api/category-rules/test', {
        method: 'POST',
        body: fields,
        accessToken,
      }),
  })
}

export function useApplyCategoryRuleNowMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id }: ApplyCategoryRuleNowRequest) =>
      apiFetch<ApplyCategoryRuleNowResponse>(`/api/category-rules/${id}/apply-now`, {
        method: 'POST',
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['transactions'] })
      void queryClient.invalidateQueries({ queryKey: ['dashboard'] })
      void queryClient.invalidateQueries({ queryKey: ['budgets'] })
    },
  })
}
