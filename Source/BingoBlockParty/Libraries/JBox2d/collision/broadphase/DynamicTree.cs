/*******************************************************************************
 * Copyright (c) 2013, Daniel Murphy
 * All rights reserved.
 * 
 * Redistribution and use in source and binary forms, with or without modification,
 * are permitted provided that the following conditions are met:
 *  * Redistributions of source code must retain the above copyright notice,
 *    this list of conditions and the following disclaimer.
 *  * Redistributions in binary form must reproduce the above copyright notice,
 *    this list of conditions and the following disclaimer in the documentation
 *    and/or other materials provided with the distribution.
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
 * ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
 * IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
 * INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
 * NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
 * PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
 * POSSIBILITY OF SUCH DAMAGE.
 ******************************************************************************/


using System;
using org.jbox2d.callbacks;
using org.jbox2d.common;
/**
 * A dynamic tree arranges data in a binary tree to accelerate queries such as volume queries and
 * ray casts. Leafs are proxies with an AABB. In the tree we expand the proxy AABB by _fatAABBFactor
 * so that the proxy AABB is bigger than the client object. This allows the client object to move by
 * small amounts without triggering a tree update.
 * 
 * @author daniel
 */
using org.jbox2d.dynamics;

namespace org.jbox2d.collision.broadphase
{
    public class DynamicTree : BroadPhaseStrategy
    {
        public static readonly int MAX_STACK_SIZE = 64;
        public static readonly int NULL_NODE = -1;
        private readonly AABB aabb = new AABB();
        private readonly Color3f color = new Color3f();
        private readonly AABB combinedAABB = new AABB();

        private readonly Vec2[] drawVecs = new Vec2[4];
        private readonly TreeNodeStack nodeStack = new TreeNodeStack(10);
        private readonly Vec2 r = new Vec2();
        private readonly RayCastInput subInput = new RayCastInput();
        private readonly Vec2 textVec = new Vec2();
        private int m_freeList;

        private int m_insertionCount;
        private int m_nodeCapacity;
        private int m_nodeCount;
        private DynamicTreeNode[] m_nodes;
        private DynamicTreeNode m_root;

        public DynamicTree()
        {
            m_root = null;
            m_nodeCount = 0;
            m_nodeCapacity = 16;
            m_nodes = new DynamicTreeNode[16];

            // Build a linked list for the free list.
            for (int i = m_nodeCapacity - 1; i >= 0; i--)
            {
                m_nodes[i] = new DynamicTreeNode(i);
                m_nodes[i].parent = (i == m_nodeCapacity - 1) ? null : m_nodes[i + 1];
                m_nodes[i].height = -1;
            }
            m_freeList = 0;

            m_insertionCount = 0;

            for (int i = 0; i < drawVecs.Length; i++)
            {
                drawVecs[i] = new Vec2();
            }
        }


        public int createProxy(AABB aabb, object userData)
        {
            DynamicTreeNode node = allocateNode();
            int proxyId = node.id;
            // Fatten the aabb
            AABB nodeAABB = node.aabb;
            nodeAABB.lowerBound.x = aabb.lowerBound.x - Settings.aabbExtension;
            nodeAABB.lowerBound.y = aabb.lowerBound.y - Settings.aabbExtension;
            nodeAABB.upperBound.x = aabb.upperBound.x + Settings.aabbExtension;
            nodeAABB.upperBound.y = aabb.upperBound.y + Settings.aabbExtension;
            node.userData = userData;

            insertLeaf(proxyId);

            return proxyId;
        }


        public void destroyProxy(int proxyId)
        {
            DynamicTreeNode node = m_nodes[proxyId];

            removeLeaf(node);
            freeNode(node);
        }


        public bool moveProxy(int proxyId, AABB aabb, Vec2 displacement)
        {
            DynamicTreeNode node = m_nodes[proxyId];

            AABB nodeAABB = node.aabb;
            // if (nodeAABB.contains(aabb)) {
            if (nodeAABB.lowerBound.x > aabb.lowerBound.x && nodeAABB.lowerBound.y > aabb.lowerBound.y
                && aabb.upperBound.x > nodeAABB.upperBound.x && aabb.upperBound.y > nodeAABB.upperBound.y)
            {
                return false;
            }

            removeLeaf(node);

            // Extend AABB
            Vec2 lowerBound = nodeAABB.lowerBound;
            Vec2 upperBound = nodeAABB.upperBound;
            lowerBound.x = aabb.lowerBound.x - Settings.aabbExtension;
            lowerBound.y = aabb.lowerBound.y - Settings.aabbExtension;
            upperBound.x = aabb.upperBound.x + Settings.aabbExtension;
            upperBound.y = aabb.upperBound.y + Settings.aabbExtension;

            // Predict AABB displacement.
            double dx = displacement.x*Settings.aabbMultiplier;
            double dy = displacement.y*Settings.aabbMultiplier;
            if (dx < 0.0d)
            {
                lowerBound.x += dx;
            }
            else
            {
                upperBound.x += dx;
            }

            if (dy < 0.0d)
            {
                lowerBound.y += dy;
            }
            else
            {
                upperBound.y += dy;
            }

            insertLeaf(proxyId);
            return true;
        }


        public object getUserData(int proxyId)
        {
            return m_nodes[proxyId].userData;
        }


        public AABB getFatAABB(int proxyId)
        {
            return m_nodes[proxyId].aabb;
        }


        public void query(TreeCallback callback, AABB aabb)
        {
            nodeStack.reset();
            nodeStack.push(m_root);

            while (nodeStack.getCount() > 0)
            {
                DynamicTreeNode node = nodeStack.pop();
                if (node == null)
                {
                    continue;
                }

                if (AABB.testOverlap(node.aabb, aabb))
                {
                    if (node.child1 == null)
                    {
                        bool proceed = callback.treeCallback(node.id);
                        if (!proceed)
                        {
                            return;
                        }
                    }
                    else
                    {
                        nodeStack.push(node.child1);
                        nodeStack.push(node.child2);
                    }
                }
            }
        }


        public void raycast(TreeRayCastCallback callback, RayCastInput input)
        {
            Vec2 p1 = input.p1;
            Vec2 p2 = input.p2;
            double p1x = p1.x, p2x = p2.x, p1y = p1.y, p2y = p2.y;
            double vx, vy;
            double rx, ry;
            double absVx, absVy;
            double cx, cy;
            double hx, hy;
            double tempx, tempy;
            r.x = p2x - p1x;
            r.y = p2y - p1y;
            r.normalize();
            rx = r.x;
            ry = r.y;

            // v is perpendicular to the segment.
            vx = -1d*ry;
            vy = 1d*rx;
            absVx = MathUtils.abs(vx);
            absVy = MathUtils.abs(vy);

            // Separating axis for segment (Gino, p80).
            // |dot(v, p1 - c)| > dot(|v|, h)

            double maxFraction = input.maxFraction;

            // Build a bounding box for the segment.
            AABB segAABB = aabb;
            // Vec2 t = p1 + maxFraction * (p2 - p1);
            // before inline
            // temp.set(p2).subLocal(p1).mulLocal(maxFraction).addLocal(p1);
            // Vec2.minToOut(p1, temp, segAABB.lowerBound);
            // Vec2.maxToOut(p1, temp, segAABB.upperBound);
            tempx = (p2x - p1x)*maxFraction + p1x;
            tempy = (p2y - p1y)*maxFraction + p1y;
            segAABB.lowerBound.x = p1x < tempx ? p1x : tempx;
            segAABB.lowerBound.y = p1y < tempy ? p1y : tempy;
            segAABB.upperBound.x = p1x > tempx ? p1x : tempx;
            segAABB.upperBound.y = p1y > tempy ? p1y : tempy;
            // end inline

            nodeStack.reset();
            nodeStack.push(m_root);
            while (nodeStack.getCount() > 0)
            {
                DynamicTreeNode node = nodeStack.pop();
                if (node == null)
                {
                    continue;
                }

                AABB nodeAABB = node.aabb;
                if (!AABB.testOverlap(nodeAABB, segAABB))
                {
                    continue;
                }

                // Separating axis for segment (Gino, p80).
                // |dot(v, p1 - c)| > dot(|v|, h)
                // node.aabb.getCenterToOut(c);
                // node.aabb.getExtentsToOut(h);
                cx = (nodeAABB.lowerBound.x + nodeAABB.upperBound.x)*.5d;
                cy = (nodeAABB.lowerBound.y + nodeAABB.upperBound.y)*.5d;
                hx = (nodeAABB.upperBound.x - nodeAABB.lowerBound.x)*.5d;
                hy = (nodeAABB.upperBound.y - nodeAABB.lowerBound.y)*.5d;
                tempx = p1x - cx;
                tempy = p1y - cy;
                double separation = MathUtils.abs(vx*tempx + vy*tempy) - (absVx*hx + absVy*hy);
                if (separation > 0.0d)
                {
                    continue;
                }

                if (node.isLeaf())
                {
                    subInput.p1.x = p1x;
                    subInput.p1.y = p1y;
                    subInput.p2.x = p2x;
                    subInput.p2.y = p2y;
                    subInput.maxFraction = maxFraction;

                    double value = callback.raycastCallback(subInput, node.id);

                    if (value == 0.0d)
                    {
                        // The client has terminated the ray cast.
                        return;
                    }

                    if (value > 0.0d)
                    {
                        // Update segment bounding box.
                        maxFraction = value;
                        // temp.set(p2).subLocal(p1).mulLocal(maxFraction).addLocal(p1);
                        // Vec2.minToOut(p1, temp, segAABB.lowerBound);
                        // Vec2.maxToOut(p1, temp, segAABB.upperBound);
                        tempx = (p2x - p1x)*maxFraction + p1x;
                        tempy = (p2y - p1y)*maxFraction + p1y;
                        segAABB.lowerBound.x = p1x < tempx ? p1x : tempx;
                        segAABB.lowerBound.y = p1y < tempy ? p1y : tempy;
                        segAABB.upperBound.x = p1x > tempx ? p1x : tempx;
                        segAABB.upperBound.y = p1y > tempy ? p1y : tempy;
                    }
                }
                else
                {
                    nodeStack.push(node.child1);
                    nodeStack.push(node.child2);
                }
            }
        }


        public int computeHeight()
        {
            return computeHeight(m_root);
        }


        public int getHeight()
        {
            if (m_root == null)
            {
                return 0;
            }
            return m_root.height;
        }


        public int getMaxBalance()
        {
            int maxBalance = 0;
            for (int i = 0; i < m_nodeCapacity; ++i)
            {
                DynamicTreeNode node = m_nodes[i];
                if (node.height <= 1)
                {
                    continue;
                }

                DynamicTreeNode child1 = node.child1;
                DynamicTreeNode child2 = node.child2;
                int balance = MathUtils.abs(child2.height - child1.height);
                maxBalance = MathUtils.max(maxBalance, balance);
            }

            return maxBalance;
        }


        public double getAreaRatio()
        {
            if (m_root == null)
            {
                return 0.0d;
            }

            DynamicTreeNode root = m_root;
            double rootArea = root.aabb.getPerimeter();

            double totalArea = 0.0d;
            for (int i = 0; i < m_nodeCapacity; ++i)
            {
                DynamicTreeNode node = m_nodes[i];
                if (node.height < 0)
                {
                    // Free node in pool
                    continue;
                }

                totalArea += node.aabb.getPerimeter();
            }

            return totalArea/rootArea;
        }

        public int getInsertionCount()
        {
            return m_insertionCount;
        }

        public void drawTree(DebugDraw argDraw)
        {
            if (m_root == null)
            {
                return;
            }
            int height = computeHeight();
            drawTree(argDraw, m_root, 0, height);
        }

        private int computeHeight(DynamicTreeNode node)
        {
            if (node.isLeaf())
            {
                return 0;
            }
            int height1 = computeHeight(node.child1);
            int height2 = computeHeight(node.child2);
            return 1 + MathUtils.max(height1, height2);
        }

        /**
   * Validate this tree. For testing.
   */

        public void validate()
        {
            validateStructure(m_root);
            validateMetrics(m_root);

            int freeCount = 0;
            DynamicTreeNode freeNode = m_freeList != NULL_NODE ? m_nodes[m_freeList] : null;
            while (freeNode != null)
            {
                freeNode = freeNode.parent;
                ++freeCount;
            }
        }

        /**
   * Build an optimal tree. Very expensive. For testing.
   */

        public void rebuildBottomUp()
        {
            var nodes = new int[m_nodeCount];
            int count = 0;

            // Build array of leaves. Free the rest.
            for (int i = 0; i < m_nodeCapacity; ++i)
            {
                if (m_nodes[i].height < 0)
                {
                    // free node in pool
                    continue;
                }

                DynamicTreeNode node = m_nodes[i];
                if (node.isLeaf())
                {
                    node.parent = null;
                    nodes[count] = i;
                    ++count;
                }
                else
                {
                    freeNode(node);
                }
            }

            var b = new AABB();
            while (count > 1)
            {
                double minCost = double.MaxValue;
                int iMin = -1, jMin = -1;
                for (int i = 0; i < count; ++i)
                {
                    AABB aabbi = m_nodes[nodes[i]].aabb;

                    for (int j = i + 1; j < count; ++j)
                    {
                        AABB aabbj = m_nodes[nodes[j]].aabb;
                        b.combine(aabbi, aabbj);
                        double cost = b.getPerimeter();
                        if (cost < minCost)
                        {
                            iMin = i;
                            jMin = j;
                            minCost = cost;
                        }
                    }
                }

                int index1 = nodes[iMin];
                int index2 = nodes[jMin];
                DynamicTreeNode child1 = m_nodes[index1];
                DynamicTreeNode child2 = m_nodes[index2];

                DynamicTreeNode parent = allocateNode();
                parent.child1 = child1;
                parent.child2 = child2;
                parent.height = 1 + MathUtils.max(child1.height, child2.height);
                parent.aabb.combine(child1.aabb, child2.aabb);
                parent.parent = null;

                child1.parent = parent;
                child2.parent = parent;

                nodes[jMin] = nodes[count - 1];
                nodes[iMin] = parent.id;
                --count;
            }

            m_root = m_nodes[nodes[0]];

            validate();
        }

        private DynamicTreeNode allocateNode()
        {
            if (m_freeList == NULL_NODE)
            {
                DynamicTreeNode[] old = m_nodes;
                m_nodeCapacity *= 2;
                m_nodes = new DynamicTreeNode[m_nodeCapacity];
                ArrayHelper.Copy(old, 0, m_nodes, 0, old.Length);

                // Build a linked list for the free list.
                for (int i = m_nodeCapacity - 1; i >= m_nodeCount; i--)
                {
                    m_nodes[i] = new DynamicTreeNode(i);
                    m_nodes[i].parent = (i == m_nodeCapacity - 1) ? null : m_nodes[i + 1];
                    m_nodes[i].height = -1;
                }
                m_freeList = m_nodeCount;
            }
            int nodeId = m_freeList;
            DynamicTreeNode treeNode = m_nodes[nodeId];
            m_freeList = treeNode.parent != null ? treeNode.parent.id : NULL_NODE;

            treeNode.parent = null;
            treeNode.child1 = null;
            treeNode.child2 = null;
            treeNode.height = 0;
            treeNode.userData = null;
            ++m_nodeCount;
            return treeNode;
        }

        /**
   * returns a node to the pool
   */

        private void freeNode(DynamicTreeNode node)
        {
            node.parent = m_freeList != NULL_NODE ? m_nodes[m_freeList] : null;
            node.height = -1;
            m_freeList = node.id;
            m_nodeCount--;
        }


        private void insertLeaf(int leaf_index)
        {
            m_insertionCount++;

            DynamicTreeNode leaf = m_nodes[leaf_index];
            if (m_root == null)
            {
                m_root = leaf;
                m_root.parent = null;
                return;
            }

            // find the best sibling
            AABB leafAABB = leaf.aabb;
            DynamicTreeNode index = m_root;
            while (index.child1 != null)
            {
                DynamicTreeNode node = index;
                DynamicTreeNode child1 = node.child1;
                DynamicTreeNode child2 = node.child2;

                double area = node.aabb.getPerimeter();

                combinedAABB.combine(node.aabb, leafAABB);
                double combinedArea = combinedAABB.getPerimeter();

                // Cost of creating a new parent for this node and the new leaf
                double cost = 2.0d*combinedArea;

                // Minimum cost of pushing the leaf further down the tree
                double inheritanceCost = 2.0d*(combinedArea - area);

                // Cost of descending into child1
                double cost1;
                if (child1.isLeaf())
                {
                    combinedAABB.combine(leafAABB, child1.aabb);
                    cost1 = combinedAABB.getPerimeter() + inheritanceCost;
                }
                else
                {
                    combinedAABB.combine(leafAABB, child1.aabb);
                    double oldArea = child1.aabb.getPerimeter();
                    double newArea = combinedAABB.getPerimeter();
                    cost1 = (newArea - oldArea) + inheritanceCost;
                }

                // Cost of descending into child2
                double cost2;
                if (child2.isLeaf())
                {
                    combinedAABB.combine(leafAABB, child2.aabb);
                    cost2 = combinedAABB.getPerimeter() + inheritanceCost;
                }
                else
                {
                    combinedAABB.combine(leafAABB, child2.aabb);
                    double oldArea = child2.aabb.getPerimeter();
                    double newArea = combinedAABB.getPerimeter();
                    cost2 = newArea - oldArea + inheritanceCost;
                }

                // Descend according to the minimum cost.
                if (cost < cost1 && cost < cost2)
                {
                    break;
                }

                // Descend
                if (cost1 < cost2)
                {
                    index = child1;
                }
                else
                {
                    index = child2;
                }
            }

            DynamicTreeNode sibling = index;
            DynamicTreeNode oldParent = m_nodes[sibling.id].parent;
            DynamicTreeNode newParent = allocateNode();
            newParent.parent = oldParent;
            newParent.userData = null;
            newParent.aabb.combine(leafAABB, sibling.aabb);
            newParent.height = sibling.height + 1;

            if (oldParent != null)
            {
                // The sibling was not the root.
                if (oldParent.child1 == sibling)
                {
                    oldParent.child1 = newParent;
                }
                else
                {
                    oldParent.child2 = newParent;
                }

                newParent.child1 = sibling;
                newParent.child2 = leaf;
                sibling.parent = newParent;
                leaf.parent = newParent;
            }
            else
            {
                // The sibling was the root.
                newParent.child1 = sibling;
                newParent.child2 = leaf;
                sibling.parent = newParent;
                leaf.parent = newParent;
                m_root = newParent;
            }

            // Walk back up the tree fixing heights and AABBs
            index = leaf.parent;
            while (index != null)
            {
                index = balance(index);

                DynamicTreeNode child1 = index.child1;
                DynamicTreeNode child2 = index.child2;

                index.height = 1 + MathUtils.max(child1.height, child2.height);
                index.aabb.combine(child1.aabb, child2.aabb);

                index = index.parent;
            }

            // validate();
        }

        private void removeLeaf(DynamicTreeNode leaf)
        {
            if (leaf == m_root)
            {
                m_root = null;
                return;
            }

            DynamicTreeNode parent = leaf.parent;
            DynamicTreeNode grandParent = parent.parent;
            DynamicTreeNode sibling;
            if (parent.child1 == leaf)
            {
                sibling = parent.child2;
            }
            else
            {
                sibling = parent.child1;
            }

            if (grandParent != null)
            {
                // Destroy parent and connect sibling to grandParent.
                if (grandParent.child1 == parent)
                {
                    grandParent.child1 = sibling;
                }
                else
                {
                    grandParent.child2 = sibling;
                }
                sibling.parent = grandParent;
                freeNode(parent);

                // Adjust ancestor bounds.
                DynamicTreeNode index = grandParent;
                while (index != null)
                {
                    index = balance(index);

                    DynamicTreeNode child1 = index.child1;
                    DynamicTreeNode child2 = index.child2;

                    index.aabb.combine(child1.aabb, child2.aabb);
                    index.height = 1 + MathUtils.max(child1.height, child2.height);

                    index = index.parent;
                }
            }
            else
            {
                m_root = sibling;
                sibling.parent = null;
                freeNode(parent);
            }

            // validate();
        }

        // Perform a left or right rotation if node A is imbalanced.
        // Returns the new root index.
        private DynamicTreeNode balance(DynamicTreeNode iA)
        {
            DynamicTreeNode A = iA;
            if (A.isLeaf() || A.height < 2)
            {
                return iA;
            }

            DynamicTreeNode iB = A.child1;
            DynamicTreeNode iC = A.child2;

            DynamicTreeNode B = iB;
            DynamicTreeNode C = iC;

            int balance = C.height - B.height;

            // Rotate C up
            if (balance > 1)
            {
                DynamicTreeNode iF = C.child1;
                DynamicTreeNode iG = C.child2;
                DynamicTreeNode F = iF;
                DynamicTreeNode G = iG;

                // Swap A and C
                C.child1 = iA;
                C.parent = A.parent;
                A.parent = iC;

                // A's old parent should point to C
                if (C.parent != null)
                {
                    if (C.parent.child1 == iA)
                    {
                        C.parent.child1 = iC;
                    }
                    else
                    {
                        C.parent.child2 = iC;
                    }
                }
                else
                {
                    m_root = iC;
                }

                // Rotate
                if (F.height > G.height)
                {
                    C.child2 = iF;
                    A.child2 = iG;
                    G.parent = iA;
                    A.aabb.combine(B.aabb, G.aabb);
                    C.aabb.combine(A.aabb, F.aabb);

                    A.height = 1 + MathUtils.max(B.height, G.height);
                    C.height = 1 + MathUtils.max(A.height, F.height);
                }
                else
                {
                    C.child2 = iG;
                    A.child2 = iF;
                    F.parent = iA;
                    A.aabb.combine(B.aabb, F.aabb);
                    C.aabb.combine(A.aabb, G.aabb);

                    A.height = 1 + MathUtils.max(B.height, F.height);
                    C.height = 1 + MathUtils.max(A.height, G.height);
                }

                return iC;
            }

            // Rotate B up
            if (balance < -1)
            {
                DynamicTreeNode iD = B.child1;
                DynamicTreeNode iE = B.child2;
                DynamicTreeNode D = iD;
                DynamicTreeNode E = iE;

                // Swap A and B
                B.child1 = iA;
                B.parent = A.parent;
                A.parent = iB;

                // A's old parent should point to B
                if (B.parent != null)
                {
                    if (B.parent.child1 == iA)
                    {
                        B.parent.child1 = iB;
                    }
                    else
                    {
                        B.parent.child2 = iB;
                    }
                }
                else
                {
                    m_root = iB;
                }

                // Rotate
                if (D.height > E.height)
                {
                    B.child2 = iD;
                    A.child1 = iE;
                    E.parent = iA;
                    A.aabb.combine(C.aabb, E.aabb);
                    B.aabb.combine(A.aabb, D.aabb);

                    A.height = 1 + MathUtils.max(C.height, E.height);
                    B.height = 1 + MathUtils.max(A.height, D.height);
                }
                else
                {
                    B.child2 = iE;
                    A.child1 = iD;
                    D.parent = iA;
                    A.aabb.combine(C.aabb, D.aabb);
                    B.aabb.combine(A.aabb, E.aabb);

                    A.height = 1 + MathUtils.max(C.height, D.height);
                    B.height = 1 + MathUtils.max(A.height, E.height);
                }

                return iB;
            }

            return iA;
        }

        private void validateStructure(DynamicTreeNode node)
        {
            if (node == null)
            {
                return;
            }

            if (node == m_root)
            {
            }

            DynamicTreeNode child1 = node.child1;
            DynamicTreeNode child2 = node.child2;

            if (node.isLeaf())
            {
                return;
            }

            validateStructure(child1);
            validateStructure(child2);
        }

        private void validateMetrics(DynamicTreeNode node)
        {
            if (node == null)
            {
                return;
            }

            DynamicTreeNode child1 = node.child1;
            DynamicTreeNode child2 = node.child2;

            if (node.isLeaf())
            {
                return;
            }

            int height1 = child1.height;
            int height2 = child2.height;
            int height;
            height = 1 + MathUtils.max(height1, height2);

            var aabb = new AABB();
            aabb.combine(child1.aabb, child2.aabb);

            validateMetrics(child1);
            validateMetrics(child2);
        }


        public void drawTree(DebugDraw argDraw, DynamicTreeNode node, int spot, int height)
        {
            node.aabb.getVertices(drawVecs);

            color.set(1, (height - spot)*1d/height, (height - spot)*1d/height);
            argDraw.drawPolygon(drawVecs, 4, color);

            argDraw.getViewportTranform().getWorldToScreen(node.aabb.upperBound, textVec);
            argDraw.drawString(textVec.x, textVec.y, node.id + "-" + (spot + 1) + "/" + height, color);

            if (node.child1 != null)
            {
                drawTree(argDraw, node.child1, spot + 1, height);
            }
            if (node.child2 != null)
            {
                drawTree(argDraw, node.child2, spot + 1, height);
            }
        }

        public class TreeNodeStack
        {
            private int position;
            private int size;
            private DynamicTreeNode[] stack;

            public TreeNodeStack(int initialSize)
            {
                stack = new DynamicTreeNode[initialSize];
                position = 0;
                size = initialSize;
            }

            public void reset()
            {
                position = 0;
            }

            public DynamicTreeNode pop()
            {
                return stack[--position];
            }

            public void push(DynamicTreeNode i)
            {
                if (position == size)
                {
                    DynamicTreeNode[] old = stack;
                    stack = new DynamicTreeNode[size*2];
                    size = stack.Length;
                    ArrayHelper.Copy(old, 0, stack, 0, old.Length);
                }
                stack[position++] = i;
            }

            public int getCount()
            {
                return position;
            }
        }
    }
}